using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WolvenKitMcp;

/// <summary>Result of a cp77tools invocation (daemon or subprocess).</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs WolvenKit commands for the MCP server.
///
/// Fast path: a persistent <c>WolvenKitDaemon</c> is started once
/// (HashService loaded just once ~6 s); subsequent requests are sent to it
/// over stdio IPC and cost a few milliseconds.
///
/// Fallback: if the daemon is unavailable, each call relaunches <c>cp77tools</c>
/// as a subprocess (original behavior, ~6 s/call but functional).
///
/// Environment variables (all optional):
///   WOLVENKIT_DAEMON               path of WolvenKitDaemon.dll
///   WOLVENKIT_CP77TOOLS            path of the cp77tools executable (fallback)
///   DOTNET_ROOT / WOLVENKIT_DOTNET_ROOT   .NET runtime root
///   WOLVENKIT_CLI_TIMEOUT_SECONDS  max delay of a command (default 300)
/// </summary>
public sealed class Cp77ToolsRunner : IDisposable
{
    private readonly string _cp77tools;
    private readonly string? _dotnetRoot;
    private readonly string _dotnetExe;
    private readonly string _daemonDll;
    private readonly TimeSpan _timeout;

    private readonly SemaphoreSlim _initLock = new(1, 1); // serializes daemon startup
    private readonly SemaphoreSlim _writeLock = new(1, 1); // serializes stdin writes
    private Process? _daemon;
    private StreamWriter? _toDaemon;
    private StreamReader? _fromDaemon;
    private Task? _readLoop;
    private bool _daemonDisabled;
    private int _nextId;

    // In-flight requests, indexed by id; populated by SendToDaemonAsync,
    // completed by the read loop when the response arrives. LastActivity
    // is refreshed on each progress message (inactivity timeout).
    private sealed class Outstanding
    {
        public required TaskCompletionSource<CliResult> Tcs { get; init; }
        public IProgress<string>? Progress;
        public long LastActivity = Environment.TickCount64;
    }

    private readonly ConcurrentDictionary<int, Outstanding> _outstanding = new();

    // Cache of archive listings (key = absolute path). Invalidated by mtime + size
    // (mtime alone can survive a very fast repack).
    private readonly ConcurrentDictionary<string, ArchiveListing> _archiveCache = new();
    private long _cacheHits;
    private long _cacheMisses;

    // Per-verb metrics (count + percentiles via circular ring of the last 100 durations).
    private readonly ConcurrentDictionary<string, RunnerMetrics> _metrics = new();

    private sealed record ArchiveListing(DateTime Mtime, long Size, IReadOnlyList<string> Entries);

    // Per-verb inactivity ceilings: heavy verbs (uncook of a large archive,
    // build of a full project) legitimately exceed the default delay. The delay
    // is re-armed by the daemon's progress — it is an INACTIVITY timeout, not a
    // total-duration one. The WOLVENKIT_CLI_TIMEOUT_SECONDS variable remains the floor.
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

    /// <summary>Stats of the archive listing cache (hits / misses since server
    /// startup, plus the current size). Exposed via
    /// <c>wolvenkit_status</c>.</summary>
    public (long hits, long misses, int entries) CacheStats
        => (Interlocked.Read(ref _cacheHits),
            Interlocked.Read(ref _cacheMisses),
            _archiveCache.Count);

    /// <summary>Snapshot of per-verb metrics (top by count). Exposed via
    /// <c>wolvenkit_status</c> under the <c>metrics</c> key.</summary>
    public IReadOnlyList<(string verb, long calls, long totalMs, long p50, long p95)> MetricsSnapshot()
        => _metrics
            .Select(kv =>
            {
                var (p50, p95, _) = kv.Value.Percentiles();
                return (kv.Key, kv.Value.Count, kv.Value.TotalMs, p50, p95);
            })
            .OrderByDescending(t => t.Count)
            .ToList();

    /// <summary>Resets the metrics counters (used by
    /// <c>clear_cache(scope=metrics|all)</c>).</summary>
    public void ResetMetrics() => _metrics.Clear();

    /// <summary>Shared instance — also used by the MCP resources.</summary>
    public static Cp77ToolsRunner Shared { get; } = new();

    public Cp77ToolsRunner()
    {
        _cp77tools = ResolveCp77Tools();
        _dotnetRoot = ResolveDotnetRoot();
        _daemonDll = ResolveDaemonDll();

        var dotnetExeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        _dotnetExe = _dotnetRoot is not null && File.Exists(Path.Combine(_dotnetRoot, dotnetExeName))
            ? Path.Combine(_dotnetRoot, dotnetExeName)
            : dotnetExeName; // otherwise: from the PATH

        _timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("WOLVENKIT_CLI_TIMEOUT_SECONDS"),
                out var s) && s > 0 ? s : 300);
    }

    public string ToolPath => _cp77tools;
    public bool ToolExists => File.Exists(_cp77tools);

    /// <summary>
    /// Returns the list of internal paths of a .archive, from the
    /// cache if present and fresh (compared to the file's <c>LastWriteTimeUtc</c>),
    /// otherwise by querying the daemon (<c>archive --list</c>) then caching
    /// the result.
    /// </summary>
    public async Task<(IReadOnlyList<string> entries, bool fromCache, CliResult raw)>
        GetArchiveListingAsync(string archivePath, CancellationToken ct)
    {
        if (!File.Exists(archivePath))
            return (Array.Empty<string>(), false, new CliResult(-1, "", $"Archive not found: {archivePath}", false));

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

    /// <summary>Clears the archive listing cache (by default all, or
    /// a specific entry).</summary>
    public void InvalidateArchiveCache(string? archivePath = null)
    {
        if (archivePath is null) _archiveCache.Clear();
        else _archiveCache.TryRemove(archivePath, out _);
    }

    /// <summary>Extracts the internal paths from an "archive --list" listing.
    /// Each non-empty line not starting with <c>[</c> and containing a
    /// separator is treated as a REDengine path.</summary>
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

    /// <summary>Runs a WolvenKit command (verb + arguments cp77tools-style).
    /// Several calls can be in flight simultaneously: they are pipelined to
    /// the daemon and re-matched by ID. The daemon, on the execution side, stays
    /// serialized (the WolvenKit libraries are not thread-safe), but the IPC no
    /// longer blocks the chaining.</summary>
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
                    // Daemon down: we kill it; it will be relaunched on the next call.
                    // If the DLL is outright missing, definitive fallback.
                    KillDaemon();
                    if (!File.Exists(_daemonDll))
                        _daemonDisabled = true;
                }
            }
            // Fallback: cp77tools subprocess.
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
        // Fast path without lock — already alive.
        if (_daemon is { HasExited: false } && _toDaemon is not null
            && _fromDaemon is not null && _readLoop is { IsCompleted: false })
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            // Re-check under lock.
            if (_daemon is { HasExited: false } && _toDaemon is not null
                && _fromDaemon is not null && _readLoop is { IsCompleted: false })
                return;

            KillDaemon();

            if (!File.Exists(_daemonDll))
                throw new FileNotFoundException("WolvenKitDaemon not found", _daemonDll);

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
                       ?? throw new InvalidOperationException("failed to start the daemon");
            _daemon = proc;
            _toDaemon = proc.StandardInput;
            _fromDaemon = proc.StandardOutput;

            // Drain the error output continuously (otherwise the pipe buffer blocks the daemon).
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await proc.StandardError.ReadLineAsync() is not null) { }
                }
                catch { /* daemon stopped */ }
            });

            // Wait for {"ready":true} (HashService warmup, ~6-8 s).
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(TimeSpan.FromSeconds(90));
            var ready = await _fromDaemon.ReadLineAsync(readyCts.Token);
            if (ready is null || !ready.Contains("\"ready\"", StringComparison.Ordinal))
                throw new InvalidOperationException("the daemon did not signal its readiness");

            // Start the read loop: routes each response by ID
            // to the corresponding TaskCompletionSource.
            _readLoop = Task.Run(ReadResponseLoopAsync);
            StartWatchdog(proc);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Watchdog: periodic ping of the daemon when it is idle. A frozen
    /// daemon (process alive but stuck) was only detected on the next request,
    /// which then ran into a full timeout; here we kill it proactively so the
    /// next call restarts on a fresh daemon. The "ping" verb is handled by
    /// the daemon outside execLock, so a long uncook does not trigger a false positive.</summary>
    private void StartWatchdog(Process proc)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                if (!ReferenceEquals(_daemon, proc) || proc.HasExited)
                    return; // daemon replaced or already dead: the watchdog follows this process
                if (!_outstanding.IsEmpty)
                    continue; // in-flight requests already attest to liveness

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

    /// <summary>Purges the server's temporary folders (wolvenkit-mcp-*, wkmcp-*)
    /// older than 24 h. The deterministic folders (read/write/inspect) are
    /// never cleaned up during a session; without this purge at startup, they
    /// accumulate indefinitely between sessions.</summary>
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
                        // Recent root: purge its old subfolders (the
                        // deterministic per-hash folders accumulate inside).
                        foreach (var child in Directory.GetDirectories(root))
                            if (Directory.GetLastWriteTimeUtc(child) < cutoff)
                                Directory.Delete(child, recursive: true);
                }
                catch { /* locked or already deleted: at the next startup */ }
            }
        }
    }

    /// <summary>Read loop for the daemon's responses. Runs for the entire
    /// lifetime of the daemon; on its death, fails all pending requests.</summary>
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
                    // Malformed line; ignore (we would lose a response, but we
                    // mainly want to avoid breaking the read loop).
                    continue;
                }

                if (result is null)
                {
                    // Progress message: re-arms the inactivity timeout and
                    // relays to the MCP client if the tool provided an IProgress.
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
        catch { /* daemon is dead */ }

        // Daemon closed: we fail all in-flight requests.
        foreach (var kvp in _outstanding)
            kvp.Value.Tcs.TrySetException(new IOException("the daemon closed its output"));
        _outstanding.Clear();
    }

    private async Task<CliResult> SendToDaemonAsync(string[] argv, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<CliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Outstanding { Tcs = tcs, Progress = progress };
        _outstanding[id] = entry;

        // Serializes stdin writes: a single writer at a time so as not to
        // interleave two JSON-lines.
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

        // INACTIVITY timeout: as long as the daemon emits progress, the verb
        // is working — we re-arm the delay instead of killing a long but alive uncook.
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
                    continue; // progress arrived: we let it work

                _outstanding.TryRemove(id, out _);
                KillDaemon();
                return new CliResult(-1, "",
                    $"Inactivity delay exceeded ({timeout.TotalSeconds:F0} s without response " +
                    "or progress) — daemon interrupted.", true);
            }
        }
    }

    private void KillDaemon()
    {
        try { _daemon?.Kill(entireProcessTree: true); } catch { /* already dead */ }
        try { _daemon?.Dispose(); } catch { /* ignore */ }
        _daemon = null;
        _toDaemon = null;
        _fromDaemon = null;
        // _readLoop will exit naturally when the stream closes and will fail
        // the in-flight requests via _outstanding. No need to wait here.
        _readLoop = null;
    }

    // ── Fallback: cp77tools subprocess ──────────────────────────────────

    private async Task<CliResult> RunViaSubprocessAsync(string[] argv, CancellationToken ct)
    {
        if (!ToolExists)
        {
            return new CliResult(-1, "",
                $"cp77tools not found: {_cp77tools}\n" +
                "Install with: dotnet tool install -g WolvenKit.CLI " +
                "(or set the WOLVENKIT_CP77TOOLS variable).", false);
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
            return new CliResult(-1, "", $"Failed to launch cp77tools: {ex.Message}", false);
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
            try { proc.Kill(entireProcessTree: true); } catch { /* already dead */ }
            return new CliResult(-1, stdout.ToString(), stderr.ToString(), true);
        }

        proc.WaitForExit(); // guarantees draining of the redirected streams
        return new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    // ── Path resolution ────────────────────────────────────────────

    /// <summary>Locates WolvenKitDaemon.dll (WOLVENKIT_DAEMON variable, otherwise sibling project).</summary>
    private static string ResolveDaemonDll()
    {
        var explicitPath = Environment.GetEnvironmentVariable("WOLVENKIT_DAEMON");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        // Default: sibling project wolvenkit-mcp/src/WolvenKitDaemon, same configuration.
        try
        {
            var net8 = new DirectoryInfo(AppContext.BaseDirectory);   // .../WolvenKitMcp/bin/<cfg>/net8.0
            var config = net8.Parent?.Name ?? "Debug";
            var src = net8.Parent?.Parent?.Parent?.Parent;            // .../src
            if (src is not null)
                return Path.Combine(src.FullName, "WolvenKitDaemon", "bin", config, "net8.0",
                    "WolvenKitDaemon.dll");
        }
        catch { /* fallback below */ }

        return "WolvenKitDaemon.dll"; // not found → subprocess fallback
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
            catch { /* invalid PATH entry */ }
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
        catch { /* fallback */ }

        return null;
    }

    public void Dispose()
    {
        KillDaemon();
        _initLock.Dispose();
        _writeLock.Dispose();
    }
}
