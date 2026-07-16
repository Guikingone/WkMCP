using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CP77Tools.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WolvenKit.Common;
using WolvenKit.Common.Interfaces;
using WolvenKit.Common.Model;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.Common.Services;
using WolvenKit.Core.Compression;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Services;
using WolvenKit.Modkit.RED4;
using WolvenKit.Modkit.RED4.Opus;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.CR2W.Archive;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

// ════════════════════════════════════════════════════════════════════════
// WkDaemon — persistent host for the WolvenKit libraries.
//
// Loads HashService ONCE (~6 s at startup), then handles successive
// requests over stdin/stdout without paying that cold-start.
//
// Protocol (JSON, one line per message):
//   request  : {"id":N,"argv":["unbundle","/x.archive","--outpath","/out"]}
//   response : {"id":N,"exit":0,"output":"[ Information ] ..."}
//   on ready : {"ready":true}
//
// Exit codes: 0 = success; -1 = argument or loading error;
// 1 = empty result / not found (e.g. loc-resolve --key absent, tweak validate with
// unknown keys, opus-import without encoding). id=0 is reserved for deserialization
// errors. Responses may arrive out of order (pipelining) while execution is
// serialized (execLock); stdout is reserved for the protocol.
//
// Special verbs (outside ConsoleFunctions):
//   loc-resolve <gameRoot|.exe> [--lang en_us] (--key <hash|secondaryKey> | --all)
//     → LocKey → localized text (M/F variants). --all lists the first 100 entries.
//       Precedence: if both --all and --key are given, --all wins (--key ignored).
//   opus-import <gameRoot|.exe> --wav-dir <dir> --out <dir>
//     → WAV (named by opus hash) → repacked .opus (embedded opusenc encoder).
// ════════════════════════════════════════════════════════════════════════

// stdout is the JSON channel reserved for the protocol: we keep a reference
// (channel) before any redirection.
var channel = Console.Out;

// Some WolvenKit tasks (the "archive --list" listing in particular)
// write their result via Console.WriteLine and not via ILoggerService. So we
// redirect Console.Out to the same buffer as the capturing logger — otherwise
// this output would be lost and archive_info / find_in_archives / the
// archive resource would return nothing.
var logger = new CapturingLoggerService();
Console.SetOut(new CapturingTextWriter(logger));

// Some WolvenKit dependencies (DirectXTexNet, texture conversion)
// are shipped as content files: copied to the build output but
// absent from deps.json, so unfindable by the default .NET resolver.
// We add a fallback that loads them from the application folder.
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var dll = Path.Combine(AppContext.BaseDirectory, (name.Name ?? "") + ".dll");
    return File.Exists(dll) ? ctx.LoadFromAssemblyPath(dll) : null;
};

Oodle.Load();

var services = new ServiceCollection();
services.AddLogging();

// In-house implementations (the WolvenKit.CLI ones are internal).
services.AddSingleton<ILoggerService>(logger);
services.AddSingleton<IProgressService<double>>(new BridgingProgressService(logger));

// Reference data — singletons kept warm (HashService = the ~6 s cost).
services.AddSingleton<IHashService, HashService>();
services.AddSingleton<ITweakDBService, TweakDBService>();
services.AddSingleton<ILocKeyService, LocKeyService>();
services.AddSingleton<IHookService, HookService>();

// Per-operation state — scoped: ArchiveManager accumulates the loaded archives,
// it MUST be fresh on each request (otherwise state leaks between calls).
services.AddScoped<IArchiveManager, ArchiveManager>();
services.AddScoped<IModTools, ModTools>();
services.AddScoped<Red4ParserService>();
services.AddScoped<MeshTools>();

services.AddOptions<CommonImportArgs>();
services.AddOptions<XbmImportArgs>();
services.AddOptions<GltfImportArgs>();
services.AddOptions<XbmExportArgs>();
services.AddOptions<MeshExportArgs>();
services.AddOptions<AnimationExportArgs>();
services.AddOptions<MorphTargetExportArgs>();
services.AddOptions<MlmaskExportArgs>();
services.AddOptions<WemExportArgs>();

services.AddScoped<ConsoleFunctions>();

var provider = services.BuildServiceProvider();
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// Pre-warm: builds HashService now (~6 s paid once).
using (var warm = provider.CreateScope())
{
    _ = warm.ServiceProvider.GetRequiredService<ConsoleFunctions>();
}
channel.WriteLine("""{"ready":true}""");
channel.Flush();

// IPC pipelining: reading requests does not block on execution. Several
// requests may be in-flight, but execution stays serialized by execLock because
// the WolvenKit libraries (logger, archive manager, captured Console.Out) are
// not thread-safe for execution. The useful overlap: decode request N+1
// while N executes, and let the client pipeline sends without waiting.
var execLock = new SemaphoreSlim(1, 1);
var writeLock = new SemaphoreSlim(1, 1);

// Emits a progress message {"id":N,"progress":"…"} on the protocol channel.
// Called synchronously by the logger sink during Dispatch.
void EmitProgress(int id, string message)
{
    if (message.Length > 300) message = message[..300] + "…";
    writeLock.Wait();
    try
    {
        channel.WriteLine(JsonSerializer.Serialize(new DaemonProgress(id, message), json));
        channel.Flush();
    }
    finally { writeLock.Release(); }
}

async Task HandleRequest(DaemonRequest req)
{
    int exit;
    string output;
    try
    {
        await execLock.WaitAsync();
        try
        {
            logger.Drain();
            // During execution (serialized by execLock), each log line
            // is relayed to the client as a progress message, throttled to 2/s —
            // so fast verbs produce nothing, while long ones (uncook, build)
            // report their progress instead of staying silent for minutes.
            var lastEmit = Environment.TickCount64;
            logger.ProgressSink = msg =>
            {
                var now = Environment.TickCount64;
                if (now - lastEmit < 500) return;
                lastEmit = now;
                EmitProgress(req.id, msg);
            };
            try { exit = await Dispatch(provider, logger, req.argv); }
            finally { logger.ProgressSink = null; }
            output = logger.Drain().Trim();
        }
        finally { execLock.Release(); }
    }
    catch (Exception ex)
    {
        exit = -1;
        // Drain the log accumulated before the exception (often the real diagnostic:
        // progress, steps) instead of returning only the bare message.
        var partial = logger.Drain().Trim();
        output = (partial.Length > 0 ? partial + "\n" : "") +
                 "[ 0: Error ] - Daemon error: " + ex.Message;
    }

    await writeLock.WaitAsync();
    try
    {
        await channel.WriteLineAsync(
            JsonSerializer.Serialize(new DaemonResponse(req.id, exit, output), json));
        await channel.FlushAsync();
    }
    finally { writeLock.Release(); }
}

var pending = new List<Task>();
string? line;
while ((line = Console.In.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;

    DaemonRequest? req;
    try
    {
        req = JsonSerializer.Deserialize<DaemonRequest>(line, json);
    }
    catch (Exception ex)
    {
        await writeLock.WaitAsync();
        try
        {
            await channel.WriteLineAsync(JsonSerializer.Serialize(
                new DaemonResponse(0, -1, "Invalid JSON request: " + ex.Message), json));
            await channel.FlushAsync();
        }
        finally { writeLock.Release(); }
        continue;
    }
    if (req is null) continue;

    // ping: liveness check from the watchdog on the MCP server side — replies immediately
    // WITHOUT taking execLock (otherwise a long uncook would make the daemon look dead).
    if (req.argv is ["ping"])
    {
        await writeLock.WaitAsync();
        try
        {
            await channel.WriteLineAsync(
                JsonSerializer.Serialize(new DaemonResponse(req.id, 0, "pong"), json));
            await channel.FlushAsync();
        }
        finally { writeLock.Release(); }
        continue;
    }

    // Lock-free read-only fast paths: pure pool lookups on warm, read-only
    // singletons (HashService / TweakDB name pool). They never touch the
    // ArchiveManager or the shared logger buffer, so routing them AROUND execLock
    // keeps cheap resolves responsive even while a long uncook holds the lock.
    // Output is byte-identical to the logger path ("[ 0: Level ] - msg").
    if (req.argv is ["resolve-hash", _, ..] or ["tweakdb-resolve", _, ..])
    {
        var fast = req; // capture for the closure
        _ = Task.Run(async () =>
        {
            string Line(string level, string s) => $"[ 0: {level} ] - {s}";
            var sb = new StringBuilder();
            try
            {
                if (fast.argv[0] == "resolve-hash")
                {
                    var hashes = provider.GetRequiredService<IHashService>();
                    foreach (var arg in fast.argv[1..])
                        sb.AppendLine(ulong.TryParse(arg, out var h)
                            ? Line("Information", hashes.Get(h) is { } p ? $"{h} = {p}" : $"{h} = (unknown hash)")
                            : Line("Error", $"invalid hash (unsigned integer expected): {arg}"));
                }
                else
                {
                    var tdb = provider.GetRequiredService<ITweakDBService>();
                    foreach (var arg in fast.argv[1..])
                        sb.AppendLine(ulong.TryParse(arg, out var h)
                            ? Line("Information", tdb.GetString(h) is { } n ? $"{h} = {n}" : $"{h} = (unknown)")
                            : Line("Error", $"invalid hash (unsigned integer expected): {arg}"));
                }
            }
            catch (Exception ex) { sb.AppendLine(Line("Error", "Daemon error: " + ex.Message)); }

            await writeLock.WaitAsync();
            try
            {
                await channel.WriteLineAsync(
                    JsonSerializer.Serialize(new DaemonResponse(fast.id, 0, sb.ToString().Trim()), json));
                await channel.FlushAsync();
            }
            finally { writeLock.Release(); }
        });
        continue;
    }

    pending.Add(Task.Run(() => HandleRequest(req)));

    // Prevents the pending list from growing indefinitely: we drain what has completed.
    pending.RemoveAll(t => t.IsCompleted);
}

await Task.WhenAll(pending);
return 0;

// ════════════════════════════════════════════════════════════════════════
// Dispatch — verb + argv → ConsoleFunctions method.
// ════════════════════════════════════════════════════════════════════════
static async Task<int> Dispatch(IServiceProvider provider, CapturingLoggerService logger, string[] argv)
{
    if (argv.Length == 0)
    {
        logger.Error("empty argv");
        return -1;
    }

    var verb = argv[0];

    if (verb is "--version" or "version")
    {
        var v = typeof(ConsoleFunctions).Assembly.GetName().Version?.ToString() ?? "unknown";
        logger.Info($"WkDaemon — WolvenKit.Modkit {v}");
        return 0;
    }

    // loc-resolve: LocKey (hash or key) → localized text via LocKeyService.
    // Loads the game archives into a fresh ArchiveManager (dedicated scope), then
    // an on-screens language, and resolves the key. --all lists the first entries.
    if (verb == "loc-resolve")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("loc-resolve: game path (root or .exe) missing"); return -1; }
        var exe = ResolveGameExe(pos[0]);
        if (!exe.Exists) { logger.Error($"game executable not found: {exe.FullName}"); return -1; }
        var lang = Enum.TryParse<EGameLanguage>(Opt(o, "--lang"), ignoreCase: true, out var lg)
            ? lg : EGameLanguage.en_us;

        using var locScope = provider.CreateScope();
        var sp = locScope.ServiceProvider;
        var am = LoadGameArchivesCached(provider, exe);
        var parser = sp.GetRequiredService<Red4ParserService>();
        var loc = new LocKeyService(parser, am) { Language = lang };
        if (!loc.LoadCurrentLanguage())
        {
            logger.Error($"failed to load language {lang} (on-screens not found?)");
            return -1;
        }

        if (o.ContainsKey("--all"))
        {
            var entries = loc.GetEntries().ToList();
            var filter = Opt(o, "--filter") ?? "";
            var limit = int.TryParse(Opt(o, "--limit"), out var requestedLimit)
                ? Math.Clamp(requestedLimit, 1, 5000) : 100;
            var offset = int.TryParse(Opt(o, "--offset"), out var requestedOffset)
                ? Math.Max(0, requestedOffset) : 0;
            var filtered = entries.Where(e => string.IsNullOrEmpty(filter)
                    || ((string?)e.SecondaryKey ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || ((string?)e.MaleVariant ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || ((string?)e.FemaleVariant ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.PrimaryKey).ToList();
            var page = filtered.Skip(offset).Take(limit).ToList();
            logger.Info($"loc-resolve: {page.Count} of {filtered.Count} entry(ies) for {lang} (offset {offset}, limit {limit})");
            foreach (var e in page)
                logger.Info($"{e.PrimaryKey} | {e.SecondaryKey} = {e.MaleVariant}");
            if (Opt(o, "--output") is { Length: > 0 } outputFile)
            {
                var full = Path.GetFullPath(outputFile);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var rows = new List<string> { "primaryKey,secondaryKey,maleVariant,femaleVariant" };
                rows.AddRange(page.Select(e => $"{e.PrimaryKey},{EscapeCsv((string?)e.SecondaryKey ?? "")},{EscapeCsv((string?)e.MaleVariant ?? "")},{EscapeCsv((string?)e.FemaleVariant ?? "")}"));
                await File.WriteAllLinesAsync(full, rows);
                logger.Success($"loc-resolve output: {full}");
            }
            return 0;
        }

        var key = Opt(o, "--key");
        if (string.IsNullOrEmpty(key)) { logger.Error("loc-resolve: --key required (uint64 hash or secondary key)"); return -1; }
        var entry = ulong.TryParse(key, out var kh) ? loc.GetEntry(kh) : loc.GetEntry(key);
        if (entry is null) { logger.Info($"loc-resolve: no entry for '{key}' ({lang})"); return 1; }
        logger.Info($"loc-resolve {lang} '{key}': male=\"{entry.MaleVariant}\" " +
                    $"female=\"{entry.FemaleVariant}\" secondaryKey=\"{entry.SecondaryKey}\"");
        return 0;
    }

    // opus-import: imports .wav files (named by opus hash) → .opus repacked into
    // a mod folder, via OpusTools.ImportWavs (embedded opusenc encoder).
    // Combines PNGs into 4K/1080 atlas images and writes inkTextureAtlas CR2W.
    if (verb == "generate-inkatlas")
    {
        if (!OperatingSystem.IsWindows()) { logger.Error("generate-inkatlas requires Windows/System.Drawing"); return -1; }
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("generate-inkatlas: PNG folder missing"); return -1; }
        return GenerateInkAtlas(logger, pos[0], Opt(o, "--mod-root"), Opt(o, "--raw-root"),
            Opt(o, "--depot-path") ?? "", Opt(o, "--name") ?? "atlas",
            int.TryParse(Opt(o, "--tile-width"), out var tw) ? tw : 0,
            int.TryParse(Opt(o, "--tile-height"), out var th) ? th : 0,
            o.ContainsKey("--overwrite"));
    }

    if (verb == "opus-import")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("opus-import: game path (root or .exe) missing"); return -1; }
        var exe = ResolveGameExe(pos[0]);
        if (!exe.Exists) { logger.Error($"game executable not found: {exe.FullName}"); return -1; }
        var wavDir = Opt(o, "--wav-dir");
        var outDir = Opt(o, "--out");
        if (string.IsNullOrEmpty(wavDir) || !Directory.Exists(wavDir)) { logger.Error($"--wav-dir not found: {wavDir}"); return -1; }
        if (string.IsNullOrEmpty(outDir)) { logger.Error("opus-import: --out required"); return -1; }
        var wavs = Directory.GetFiles(wavDir, "*.wav").ToList();
        if (wavs.Count == 0) { logger.Error($"no .wav in {wavDir} (names must be opus hashes, e.g. 123456.wav)"); return -1; }
        Directory.CreateDirectory(outDir);

        var am = LoadGameArchivesCached(provider, exe);
        var ok = OpusTools.ImportWavs(am, wavs, new DirectoryInfo(wavDir), new DirectoryInfo(outDir));
        logger.Info($"opus-import: {wavs.Count} wav → {outDir} ({(ok ? "OK" : "failure")})");
        return ok ? 0 : 1;
    }

    // export-entity: exports an entity appearance (.ent) to glTF via IModTools.
    // Loads the game archives (appearances reference base meshes/materials).
    if (verb == "export-entity")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("export-entity: .ent file missing"); return -1; }
        var entFile = pos[0];
        var outFile = Opt(o, "--out");
        var appearance = Opt(o, "--appearance") ?? "";
        var gameArg = Opt(o, "--game");
        if (string.IsNullOrEmpty(outFile)) { logger.Error("export-entity: --out required"); return -1; }
        if (!File.Exists(entFile)) { logger.Error($".ent not found: {entFile}"); return -1; }

        using var entScope = provider.CreateScope();
        var sp = entScope.ServiceProvider;
        var am = sp.GetRequiredService<IArchiveManager>();
        if (!string.IsNullOrEmpty(gameArg))
        {
            var exe = ResolveGameExe(gameArg);
            if (!exe.Exists) { logger.Error($"game executable not found: {exe.FullName}"); return -1; }
            am.LoadGameArchives(exe);
        }
        var parser = sp.GetRequiredService<Red4ParserService>();
        var mt = sp.GetRequiredService<IModTools>();
        using var efs = File.OpenRead(entFile);
        if (!parser.TryReadRed4File(efs, out var entCr2w) || entCr2w is null)
        { logger.Error("reading the .ent CR2W failed"); return -1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        var ok = mt.ExportEntity(entCr2w, appearance, new FileInfo(outFile));
        logger.Info($"export-entity: {entFile} [{appearance}] → {outFile} ({(ok ? "OK" : "failure")})");
        return ok ? 0 : 1;
    }

    // export-materials: exports the materials of a .mesh to JSON+textures via IModTools.
    if (verb == "export-materials")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("export-materials: .mesh file missing"); return -1; }
        var meshFile = pos[0];
        var outFile = Opt(o, "--out");
        var gameArg = Opt(o, "--game");
        if (string.IsNullOrEmpty(outFile)) { logger.Error("export-materials: --out required"); return -1; }
        if (!File.Exists(meshFile)) { logger.Error($".mesh not found: {meshFile}"); return -1; }

        using var matScope = provider.CreateScope();
        var sp = matScope.ServiceProvider;
        var am = sp.GetRequiredService<IArchiveManager>();
        if (!string.IsNullOrEmpty(gameArg))
        {
            var exe = ResolveGameExe(gameArg);
            if (!exe.Exists) { logger.Error($"game executable not found: {exe.FullName}"); return -1; }
            am.LoadGameArchives(exe);
        }
        var parser = sp.GetRequiredService<Red4ParserService>();
        var mt = sp.GetRequiredService<IModTools>();
        using var mfs = File.OpenRead(meshFile);
        if (!parser.TryReadRed4File(mfs, out var meshCr2w) || meshCr2w is null)
        { logger.Error("reading the .mesh CR2W failed"); return -1; }
        var outFi = new FileInfo(outFile);
        Directory.CreateDirectory(outFi.DirectoryName!);
        // ExportMaterials needs a material repo (where to write/resolve the
        // textures); without MaterialRepo the export fails. We use the output folder.
        var mea = new MeshExportArgs
        {
            MaterialRepo = outFi.DirectoryName,
            MaterialUncookExtension = EUncookExtension.png,
            withMaterials = true,
        };
        var ok = mt.ExportMaterials(meshCr2w, outFi, mea);
        logger.Info($"export-materials: {meshFile} → {outFile} ({(ok ? "OK" : "failure")})");
        return ok ? 0 : 1;
    }

    // resolve-hash: reverse hash → path lookup, via HashService (already warm).
    if (verb == "resolve-hash")
    {
        var hashes = provider.GetRequiredService<IHashService>();
        foreach (var arg in argv[1..])
        {
            if (ulong.TryParse(arg, out var h))
            {
                var path = hashes.Get(h);
                logger.Info(path is not null ? $"{h} = {path}" : $"{h} = (unknown hash)");
            }
            else
            {
                logger.Error($"invalid hash (unsigned integer expected): {arg}");
            }
        }
        return 0;
    }

    // tweakdb-resolve: TweakDB identifier hash → name (TweakDBIDPool, already warm).
    if (verb == "tweakdb-resolve")
    {
        var tdb = provider.GetRequiredService<ITweakDBService>();
        foreach (var arg in argv[1..])
        {
            if (ulong.TryParse(arg, out var h))
            {
                var name = tdb.GetString(h);
                logger.Info(name is not null ? $"{h} = {name}" : $"{h} = (unknown)");
            }
            else
            {
                logger.Error($"invalid hash (unsigned integer expected): {arg}");
            }
        }
        return 0;
    }

    // tweakdb-query: loads a tweakdb.bin and lists filtered records/flats.
    if (verb == "tweakdb-query")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count < 1)
        {
            logger.Error("tweakdb-query: tweakdb.bin path missing");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, pos[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"failed to load the TweakDB: {pos[0]}");
            return -1;
        }
        var filter = Opt(o, "--filter") ?? (pos.Count > 1 ? pos[1] : "");
        var limit = int.TryParse(Opt(o, "--limit"), out var requestedLimit)
            ? Math.Clamp(requestedLimit, 1, 5000) : 100;
        var offset = int.TryParse(Opt(o, "--offset"), out var requestedOffset)
            ? Math.Max(0, requestedOffset) : 0;
        var outputFile = Opt(o, "--output");

        var allRecords = TweakDBService.GetRecords()
            .Select(r => r.ToString() ?? "")
            .Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s).ToList();
        var records = allRecords.Skip(offset).Take(limit).ToList();
        var recordsTruncated = offset + records.Count < allRecords.Count;
        var allFlats = TweakDBService.GetFlats()
            .Select(flt => flt.ToString() ?? "")
            .Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s).ToList();
        var flats = allFlats.Skip(offset).Take(limit).ToList();
        var flatsTruncated = offset + flats.Count < allFlats.Count;
        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            var full = Path.GetFullPath(outputFile);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var rows = new List<string> { "kind,identifier" };
            rows.AddRange(records.Select(x => $"record,{EscapeCsv(x)}"));
            rows.AddRange(flats.Select(x => $"flat,{EscapeCsv(x)}"));
            await File.WriteAllLinesAsync(full, rows);
            logger.Success($"tweakdb-query output: {full}");
        }
        // "+" marker read on the MCP side to expose a truncated field in the JSON output.
        var advice = recordsTruncated || flatsTruncated ? " — request the next offset or refine the filter" : "";
        logger.Info(
            $"{records.Count}{(recordsTruncated ? "+" : "")} record(s), " +
            $"{flats.Count}{(flatsTruncated ? "+" : "")} flat(s) — " +
            $"filter \"{filter}\" (offset {offset}, limit {limit}, totals {allRecords.Count}/{allFlats.Count}{advice})");
        foreach (var rec in records) logger.Info("record  " + rec);
        foreach (var flt in flats) logger.Info("flat    " + flt);
        return 0;
    }

    // tweakdb-describe: for a TweakDB identifier, lists all its flats with
    // their types and current values. Indispensable before write_tweak.
    if (verb == "tweakdb-describe")
    {
        var rest = argv[1..];
        if (rest.Length < 2)
        {
            logger.Error("tweakdb-describe: tweakdbBin and recordId required");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"failed to load TweakDB: {rest[0]}");
            return -1;
        }

        var recordId = rest[1];
        var prefix = recordId + ".";

        // O(1) lookups via the per-record index (built on the first request).
        EnsureTweakDbIndex();
        if (DaemonState.RecordsByName is null
            || !DaemonState.RecordsByName.TryGetValue(recordId, out var recordTdbId))
        {
            logger.Warning($"unknown record in TweakDB: {recordId}");
            return 0;
        }

        string? recordType = null;
        if (TweakDBService.TryGetType(recordTdbId, out var t))
            recordType = t?.Name;
        logger.Info($"record {recordId} ({recordType ?? "?"})");

        int found = 0;
        var elemBudget = 0;
        var recordFlats = DaemonState.FlatsByRecord is not null
            && DaemonState.FlatsByRecord.TryGetValue(recordId, out var rf)
            ? rf : new List<WolvenKit.RED4.Types.TweakDBID>();
        foreach (var flat in recordFlats)
        {
            var text = flat.GetResolvedText();
            if (text is null || !text.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var fieldName = text.Substring(prefix.Length);
            var value = TweakDBService.GetFlat(flat);
            var typeName = value?.GetType().Name ?? "?";
            var valueStr = value?.ToString() ?? "(null)";
            // Truncate very long values (massive arrays).
            if (valueStr.Length > 200) valueStr = valueStr[..200] + "…";
            logger.Info($"  flat  {fieldName} : {typeName} = {valueStr}");
            found++;

            // For array flats, also emit each element on its own stable line so a
            // dry-run preview can compute add/remove diffs (the ToString above is
            // truncated and not parseable). Bounded per-flat and per-record.
            if (value is System.Collections.IEnumerable seq and not string)
            {
                var idx = 0;
                foreach (var el in seq)
                {
                    if (idx >= 256 || elemBudget >= 2000)
                    {
                        logger.Info($"  elem  {fieldName} [..] = … (truncated)");
                        break;
                    }
                    logger.Info($"  elem  {fieldName} [{idx}] = {el}");
                    idx++;
                    elemBudget++;
                }
            }
        }
        logger.Info($"{found} flat(s) under {recordId}");
        return 0;
    }

    // tweakdb-clone: emits a TweakXL .tweak that clones an existing record under a new id.
    // Uses the documented `$base` attribute (TweakXL copies every property value of the source
    // record at load — a faithful clone), then appends a COMMENTED inventory of the base flats
    // with their current values (resolved TweakDBIDs, invariant-culture floats, array contents)
    // so the author sees exactly what is inherited and uncomments only what they want to override.
    // Optional overrides JSON (field → value) is emitted as active keys.
    if (verb == "tweakdb-clone")
    {
        var rest = argv[1..];
        if (rest.Length < 4)
        {
            logger.Error("tweakdb-clone: tweakdbBin, baseId, newId and outputTweakFile required");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"failed to load TweakDB: {rest[0]}");
            return -1;
        }

        var baseId = rest[1];
        var newId = rest[2];
        var outputTweakFile = rest[3];
        var overridesJsonFile = rest.Length > 4 ? rest[4] : null;

        EnsureTweakDbIndex();
        if (DaemonState.RecordsByName is null
            || !DaemonState.RecordsByName.TryGetValue(baseId, out var baseTdbId))
        {
            logger.Error($"unknown base record in TweakDB: {baseId} " +
                         "(check the id with tweakdb_query / find_record_by_name)");
            return 1;
        }
        string? baseType = null;
        if (TweakDBService.TryGetType(baseTdbId, out var bt)) baseType = bt?.Name;

        // Parse optional overrides (field → scalar value) emitted as active YAML keys.
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(overridesJsonFile) && File.Exists(overridesJsonFile))
        {
            try
            {
                using var od = JsonDocument.Parse(await File.ReadAllTextAsync(overridesJsonFile));
                if (od.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in od.RootElement.EnumerateObject())
                        overrides[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }
            catch (Exception ex) { logger.Warning($"ignoring invalid overrides JSON: {ex.Message}"); }
        }

        var prefix = baseId + ".";
        var recordFlats = DaemonState.FlatsByRecord is not null
            && DaemonState.FlatsByRecord.TryGetValue(baseId, out var rf)
            ? rf : new List<WolvenKit.RED4.Types.TweakDBID>();

        var sb = new StringBuilder();
        sb.Append(newId).AppendLine(":");
        sb.Append("  $base: ").AppendLine(baseId);   // faithful clone: TweakXL copies all base flats
        foreach (var kv in overrides.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("  ").Append(kv.Key).Append(": ").AppendLine(kv.Value);

        // Commented inventory of inherited flats with their current values.
        var inventory = new List<string>();
        foreach (var flat in recordFlats)
        {
            var text = flat.GetResolvedText();
            if (text is null || !text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var field = text.Substring(prefix.Length);
            if (overrides.ContainsKey(field)) continue; // already an active override
            var value = TweakDBService.GetFlat(flat);
            inventory.Add($"{field}: {RenderTweakFlatValue(value)}");
        }
        inventory.Sort(StringComparer.Ordinal);
        if (inventory.Count > 0)
        {
            sb.AppendLine("  # ── inherited from base via $base (uncomment + edit to override) ──");
            foreach (var line in inventory) sb.Append("  # ").AppendLine(line);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputTweakFile) ?? ".");
        await File.WriteAllTextAsync(outputTweakFile, sb.ToString());
        logger.Info($"tweakdb-clone: {newId} $base {baseId} ({baseType ?? "?"}) → {outputTweakFile} " +
                    $"({inventory.Count} inherited flat(s) inventoried, {overrides.Count} override(s))");
        return 0;
    }

    // tweakdb-extract-localization: extracts all "displayName" and
    // "localizedDescription" (and variant) flats known to the TweakDB, indexed by
    // recordId. JSON output, ready to serve as a base for a translation mod.
    if (verb == "tweakdb-extract-localization")
    {
        var rest = argv[1..];
        if (rest.Length < 2)
        {
            logger.Error("tweakdb-extract-localization: tweakdbBin and outputJson required");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"failed to load TweakDB: {rest[0]}");
            return -1;
        }
        var outputJson = rest[1];
        var filter = rest.Length > 2 ? rest[2] : ""; // e.g. "Items." to narrow down

        var localizable = new HashSet<string>(StringComparer.Ordinal)
        {
            "displayName", "localizedDescription", "description",
            "uiName", "name", "menuName", "shortDescription", "longDescription",
        };

        // Index flats by recordId as in dump-records.
        var byRecord = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var flat in TweakDBService.GetFlats())
        {
            var text = flat.GetResolvedText();
            if (string.IsNullOrEmpty(text)) continue;
            var idx = text.LastIndexOf('.');
            if (idx <= 0) continue;
            var field = text[(idx + 1)..];
            if (!localizable.Contains(field)) continue;
            var recordId = text[..idx];
            if (filter.Length > 0
                && !recordId.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = TweakDBService.GetFlat(flat)?.ToString() ?? "";
            // Truncate very long values (possibly verbose descriptions).
            if (value.Length > 1000) value = value[..1000] + "…";
            if (!byRecord.TryGetValue(recordId, out var fields))
            {
                fields = new Dictionary<string, string>(StringComparer.Ordinal);
                byRecord[recordId] = fields;
            }
            fields[field] = value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputJson) ?? ".");
        var sorted = byRecord
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        await File.WriteAllTextAsync(outputJson,
            JsonSerializer.Serialize(sorted,
                new JsonSerializerOptions { WriteIndented = true }));

        logger.Info($"tweakdb-extract-localization: {byRecord.Count} record(s) with " +
                    $"translatable fields → {outputJson}" +
                    (filter.Length > 0 ? $" (filter: {filter})" : ""));
        return 0;
    }

    // tweakdb-dump-records: exports all records of a given type to
    // JSON Lines or CSV. First indexes flats by recordId to stay at
    // O(records × flats_per_record) instead of O(records × total_N_flats).
    if (verb == "tweakdb-dump-records")
    {
        var rest = argv[1..];
        if (rest.Length < 3)
        {
            logger.Error("tweakdb-dump-records: tweakdbBin, recordType, outputFile required");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"failed to load TweakDB: {rest[0]}");
            return -1;
        }

        var recordType = rest[1];
        var outputFile = rest[2];
        var format = rest.Length > 3 ? rest[3].ToLowerInvariant() : "jsonl";

        // Index flats by recordId (prefix before the last dot).
        var flatsByRecord = new Dictionary<string, List<(string field, WolvenKit.RED4.Types.TweakDBID flat)>>(
            StringComparer.Ordinal);
        foreach (var flat in TweakDBService.GetFlats())
        {
            var text = flat.GetResolvedText();
            if (string.IsNullOrEmpty(text)) continue;
            var idx = text.LastIndexOf('.');
            if (idx <= 0) continue;
            var recordId = text[..idx];
            var field = text[(idx + 1)..];
            if (!flatsByRecord.TryGetValue(recordId, out var list))
            {
                list = new List<(string, WolvenKit.RED4.Types.TweakDBID)>();
                flatsByRecord[recordId] = list;
            }
            list.Add((field, flat));
        }

        // Look up the records of the requested type.
        var matching = new List<(string id, WolvenKit.RED4.Types.TweakDBID rec)>();
        foreach (var rec in TweakDBService.GetRecords())
        {
            if (TweakDBService.TryGetType(rec, out var t) && t?.Name == recordType)
            {
                var id = rec.GetResolvedText();
                if (!string.IsNullOrEmpty(id)) matching.Add((id, rec));
            }
        }
        if (matching.Count == 0)
        {
            logger.Warning($"No record of type: {recordType}");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile);

        int written = 0;
        if (format == "csv")
        {
            // Collect columns in two passes for a stable superset.
            var rows = new List<Dictionary<string, string>>();
            var allColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, _) in matching)
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = id };
                if (flatsByRecord.TryGetValue(id, out var list))
                {
                    foreach (var (field, flat) in list)
                    {
                        var value = TweakDBService.GetFlat(flat)?.ToString() ?? "";
                        if (value.Length > 200) value = value[..200] + "…";
                        row[field] = value;
                        allColumns.Add(field);
                    }
                }
                rows.Add(row);
            }
            var cols = new List<string> { "id" };
            cols.AddRange(allColumns.OrderBy(c => c, StringComparer.Ordinal));
            writer.WriteLine(string.Join(",", cols.Select(EscapeCsv)));
            foreach (var row in rows)
            {
                writer.WriteLine(string.Join(",", cols.Select(c =>
                    EscapeCsv(row.TryGetValue(c, out var v) ? v : ""))));
                written++;
            }
        }
        else
        {
            // JSON Lines (1 record per line).
            var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
            foreach (var (id, _) in matching)
            {
                var obj = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id };
                if (flatsByRecord.TryGetValue(id, out var list))
                {
                    foreach (var (field, flat) in list)
                    {
                        var raw = TweakDBService.GetFlat(flat);
                        var s = raw?.ToString();
                        if (s is not null && s.Length > 200) s = s[..200] + "…";
                        obj[field] = s;
                    }
                }
                writer.WriteLine(JsonSerializer.Serialize(obj, jsonOpts));
                written++;
            }
        }

        logger.Info($"dump_records: {recordType} → {outputFile} " +
                    $"({written} line(s), format={format})");
        return 0;
    }

    // tweak: structured editing of .tweak files (TweakXL — YAML).
    if (verb == "tweak")
    {
        var sub = argv.Length > 1 ? argv[1] : "";
        var rest = argv[2..];
        switch (sub)
        {
            case "read":
                if (rest.Length < 2)
                {
                    logger.Error("tweak read: tweakFile and outputJsonFile required");
                    return -1;
                }
                return await TweakRead(logger, rest[0], rest[1]);
            case "write":
                if (rest.Length < 2)
                {
                    logger.Error("tweak write: jsonFile and outputTweakFile required");
                    return -1;
                }
                return await TweakWrite(logger, rest[0], rest[1]);
            case "validate":
                if (rest.Length < 2)
                {
                    logger.Error("tweak validate: tweakFile and tweakdbBin required");
                    return -1;
                }
                return await TweakValidate(provider, logger, rest[0], rest[1]);
            default:
                logger.Error($"tweak: unknown subcommand: {sub} (read | write | validate)");
                return -1;
        }
    }

    using var scope = provider.CreateScope();
    var f = scope.ServiceProvider.GetRequiredService<ConsoleFunctions>();

    switch (verb)
    {
        case "hash":
            return f.HashTask(argv[1..]);

        case "archive":
        {
            var (pos, o) = ParseArgs(argv, 1);
            return f.ArchiveTask(pos.Select(Fs).ToArray(),
                Opt(o, "--pattern") ?? "", Opt(o, "--regex") ?? "",
                o.ContainsKey("--diff"), o.ContainsKey("--list"));
        }

        case "unbundle":
        {
            var (pos, o) = ParseArgs(argv, 1);
            return f.UnbundleTask(pos.Select(Fs).ToArray(), new UnbundleTaskOptions
            {
                outpath = Dir(o, "--outpath"),
                gamepath = Opt(o, "--gamepath"),
                hash = Opt(o, "--hash"),
                pattern = Opt(o, "--pattern"),
                regex = Opt(o, "--regex"),
                DEBUG_decompress = o.ContainsKey("--debug-decompress"),
            });
        }

        case "uncook":
        {
            var (pos, o) = ParseArgs(argv, 1);
            // UncookTaskOptions has init-only properties — all in a single initializer.
            MeshExporterType? met = Enum.TryParse<MeshExporterType>(
                Opt(o, "--mesh-exporter-type"), ignoreCase: true, out var metVal) ? metVal : null;
            MeshExportType? mxt = Enum.TryParse<MeshExportType>(
                Opt(o, "--mesh-export-type"), ignoreCase: true, out var mxtVal) ? mxtVal : null;
            // Voice-over audio: --opus-export-all extracts all .opus from the archive;
            // --opus-hashes 12345,67890 targets specific opus hashes (uint32).
            var opusHashes = ParseUintList(Opt(o, "--opus-hashes"));
            return f.UncookTask(pos.Select(Fs).ToArray(), new UncookTaskOptions
            {
                outpath = Dir(o, "--outpath"),
                gamepath = Dir(o, "--gamepath"),
                raw = Dir(o, "--raw"),
                pattern = Opt(o, "--pattern"),
                regex = Opt(o, "--regex"),
                uext = ParseUext(Opt(o, "--uext")),
                hash = ulong.TryParse(Opt(o, "--hash"), out var uncookHash) ? uncookHash : 0,
                unbundle = o.ContainsKey("--unbundle"),
                forcebuffers = ParseCookedFormats(Opt(o, "--forcebuffers")),
                serialize = o.ContainsKey("--serialize") ? true : null,
                meshExporterType = met,
                meshExportType = mxt,
                meshExportMaterialRepo = Opt(o, "--mesh-export-material-repo"),
                meshExportLodFilter = o.ContainsKey("--mesh-export-lod-filter") ? true : null,
                meshExportExperimentalMergedExport = o.ContainsKey("--mesh-export-experimental-merged-export") ? true : null,
                opusExportAll = o.ContainsKey("--opus-export-all") ? true : null,
                opusHashes = opusHashes,
            });
        }

        case "export":
        {
            var (pos, o) = ParseArgs(argv, 1);
            if (o.ContainsKey("--configured"))
                return ExportConfigured(provider, logger, pos.Select(Fs).ToArray(), o);
            return f.ExportTask(pos.Select(Fs).ToArray(), new ExportTaskOptions
            {
                outpath = Dir(o, "--outpath"),
                gamepath = Opt(o, "--gamepath"),
                uext = ParseUext(Opt(o, "--uext")),
                forcebuffers = ParseCookedFormats(Opt(o, "--forcebuffers")),
            });
        }

        case "import":
        {
            var (pos, o) = ParseArgs(argv, 1);
            var importOut = Dir(o, "--outpath") ?? new DirectoryInfo(".");
            var importKeep = o.ContainsKey("--keep");
            // Configuration options need a custom GlobalImportArgs instance; the stock
            // ConsoleFunctions.ImportTask constructs its arguments from defaults.
            if (o.ContainsKey("--garment"))
                o["--garment-support"] = "true"; // backwards-compatible alias
            if (o.ContainsKey("--configured") || o.ContainsKey("--garment"))
                return await ImportConfigured(provider, logger, pos.Select(Fs).ToArray(), importOut, importKeep, o);
            return await f.ImportTask(pos.Select(Fs).ToArray(), importOut, importKeep);
        }

        case "pack":
        {
            var (pos, o) = ParseArgs(argv, 1);
            return f.PackTask(pos.Select(p => new DirectoryInfo(p)).ToArray(),
                Dir(o, "--outpath") ?? new DirectoryInfo("."));
        }

        case "build":
        {
            var (pos, _) = ParseArgs(argv, 1);
            return f.BuildTask(pos.Select(p => new DirectoryInfo(p)).ToArray());
        }

        case "convert":
        {
            var sub = argv.Length > 1 ? argv[1] : "";
            var (pos, o) = ParseArgs(argv, 2);
            return await f.Cr2wTask(pos.Select(Fs).ToArray(),
                Dir(o, "--outpath") ?? new DirectoryInfo("."),
                deserialize: sub is "deserialize" or "deserialise" or "d",
                serialize: sub is "serialize" or "serialise" or "s",
                Opt(o, "--pattern") ?? "", Opt(o, "--regex") ?? "",
                ETextConvertFormat.json, print: false);
        }

        case "conflicts":
        {
            var (pos, o) = ParseArgs(argv, 1);
            return f.ConflictsTask(new DirectoryInfo(pos.FirstOrDefault() ?? "."),
                o.ContainsKey("--structured"));
        }

        case "oodle":
        {
            var sub = argv.Length > 1 ? argv[1] : "";
            var (pos, _) = ParseArgs(argv, 2);
            if (pos.Count < 2)
            {
                logger.Error("oodle: input/output paths missing");
                return -1;
            }
            return f.OodleTask(new FileInfo(pos[0]), new FileInfo(pos[1]),
                decompress: sub == "decompress", compress: sub == "compress");
        }

        case "wwise":
        {
            var (pos, o) = ParseArgs(argv, 1);
            if (pos.Count < 2)
            {
                logger.Error("wwise: input/output paths missing");
                return -1;
            }
            return f.WwiseTask(new FileInfo(pos[0]), new FileInfo(pos[1]), o.ContainsKey("--wem"));
        }

        default:
            logger.Error($"unknown verb: {verb}");
            return -1;
    }
}

static int ExportConfigured(IServiceProvider provider, CapturingLoggerService logger,
    FileSystemInfo[] paths, Dictionary<string, string?> options)
{
    var outDir = Dir(options, "--outpath");
    if (outDir is null) { logger.Error("export --configured: --outpath is required"); return -1; }
    Directory.CreateDirectory(outDir.FullName);

    using var scope = provider.CreateScope();
    var sp = scope.ServiceProvider;
    var mt = sp.GetRequiredService<IModTools>();
    var am = sp.GetRequiredService<IArchiveManager>();
    var gamePath = Opt(options, "--gamepath");
    if (!string.IsNullOrWhiteSpace(gamePath))
    {
        var exe = ResolveGameExe(gamePath);
        if (!exe.Exists) { logger.Error($"game executable not found: {exe.FullName}"); return -1; }
        am.LoadGameArchives(exe);
    }

    var xbm = new XbmExportArgs();
    var mesh = new MeshExportArgs();
    var morph = new MorphTargetExportArgs();
    var mlmask = new MlmaskExportArgs();
    var anim = new AnimationExportArgs();
    var general = new GeneralExportArgs();

    bool SetBool(string key, Action<bool> setter)
    {
        var raw = Opt(options, key);
        if (raw is null) return true;
        if (!bool.TryParse(raw, out var value))
        {
            logger.Error($"{key}: expected true or false, got '{raw}'");
            return false;
        }
        setter(value);
        return true;
    }

    bool SetEnum<T>(string key, Action<T> setter) where T : struct, Enum
    {
        var raw = Opt(options, key);
        if (raw is null) return true;
        if (!Enum.TryParse<T>(raw, true, out var value))
        {
            logger.Error($"{key}: unknown {typeof(T).Name} value '{raw}'");
            return false;
        }
        setter(value);
        return true;
    }

    var uext = ParseUext(Opt(options, "--uext"));
    if (uext is not null)
    {
        xbm.UncookExtension = uext.Value;
        mlmask.UncookExtension = uext.Value;
        mesh.MaterialUncookExtension = uext.Value;
    }
    var materialRepo = Opt(options, "--mesh-export-material-repo");
    mesh.MaterialRepo = materialRepo;
    general.MaterialRepositoryPath = materialRepo;

    if (!SetEnum<MeshExporterType>("--mesh-exporter-type", v => mesh.MeshExporter = v)
        || !SetEnum<MeshExportType>("--mesh-export-type", v => mesh.meshExportType = v)
        || !SetBool("--mesh-lod-filter", v => mesh.LodFilter = v)
        || !SetBool("--mesh-binary", v => mesh.isGLBinary = v)
        || !SetBool("--mesh-materials", v => mesh.withMaterials = v)
        || !SetBool("--mesh-garment", v => mesh.ExportGarmentSupport = v)
        || !SetBool("--mesh-merged", v => mesh.ExperimentalMergedExport = v)
        || !SetBool("--animation-binary", v => anim.IsBinary = v)
        || !SetBool("--animation-additive-relative", v => anim.AdditiveRelativeToLocalTransform = v)
        || !SetBool("--animation-root-motion", v => anim.incRootMotion = v)
        || !SetBool("--morph-binary", v => morph.IsBinary = v)
        || !SetBool("--morph-textures", v => morph.ExportTextures = v))
        return -1;

    var exportArgs = new GlobalExportArgs().Register(
        general, xbm, mesh, morph, mlmask, new WemExportArgs(), anim);
    var forceBuffers = ParseCookedFormats(Opt(options, "--forcebuffers"));
    var exports = new List<(FileInfo File, DirectoryInfo BaseDir)>();
    foreach (var path in paths)
    {
        if (path is FileInfo file && file.Exists)
            exports.Add((file, file.Directory ?? new DirectoryInfo(".")));
        else if (path is DirectoryInfo dir && dir.Exists)
            exports.AddRange(dir.EnumerateFiles("*", SearchOption.AllDirectories).Select(file => (file, dir)));
        else
            logger.Warning($"export --configured skips missing path: {path.FullName}");
    }

    var errors = 0;
    foreach (var (file, baseDir) in exports)
    {
        if (mt.Export(file, exportArgs, baseDir, outDir, forceBuffers))
            logger.Success($"Successfully exported (configured) {file.FullName}");
        else { logger.Error($"Failed to export {file.FullName}"); errors++; }
    }
    if (exports.Count == 0) return -1;
    return errors > 0 ? 1 : 0;
}

[SupportedOSPlatform("windows")]
static int GenerateInkAtlas(CapturingLoggerService logger, string pngFolder,
    string? modRoot, string? rawRoot, string depotPath, string atlasName,
    int tileWidth, int tileHeight, bool overwrite)
{
    if (!Directory.Exists(pngFolder)) { logger.Error($"PNG folder not found: {pngFolder}"); return -1; }
    if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(rawRoot))
    { logger.Error("generate-inkatlas: --mod-root and --raw-root are required"); return -1; }
    if (tileWidth < 0 || tileHeight < 0)
    { logger.Error("generate-inkatlas: tile dimensions cannot be negative"); return -1; }
    var pngFiles = Directory.EnumerateFiles(pngFolder, "*.png", SearchOption.TopDirectoryOnly)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    if (pngFiles.Count == 0) { logger.Error("The source folder contains no PNG files."); return -1; }

    atlasName = Path.GetFileNameWithoutExtension(atlasName);
    var relativeDir = depotPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    var modDir = Path.Combine(modRoot, relativeDir);
    var rawDir = Path.Combine(rawRoot, relativeDir);
    var atlasFile = Path.Combine(modDir, atlasName + ".inkatlas");
    var imageFile = Path.Combine(rawDir, atlasName + ".png");
    var image1080File = Path.Combine(rawDir, atlasName + "_1080.png");
    if (!overwrite && new[] { atlasFile, imageFile, image1080File }.Any(File.Exists))
    { logger.Error("Output already exists; pass --overwrite to replace the atlas files."); return -1; }

    const int maxWidth = 2048;
    var images = new List<AtlasSource>();
    try
    {
        foreach (var file in pngFiles)
        {
            using var source = new Bitmap(file);
            var (width, height) = ScaleToFit(source.Width, source.Height, tileWidth, tileHeight);
            var paddedWidth = width + (width % 2);
            var paddedHeight = height + (height % 2);
            var bitmap = new Bitmap(paddedWidth, paddedHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, width, height);
            }
            images.Add(new AtlasSource(Path.GetFileNameWithoutExtension(file), bitmap, width, height));
        }

        var rows = new List<List<AtlasSource>>();
        var row = new List<AtlasSource>();
        var rowWidth = 0;
        foreach (var image in images.OrderByDescending(x => x.Bitmap.Height))
        {
            if (row.Count > 0 && rowWidth + image.Bitmap.Width > maxWidth)
            { rows.Add(row); row = new List<AtlasSource>(); rowWidth = 0; }
            row.Add(image);
            rowWidth += image.Bitmap.Width;
        }
        if (row.Count > 0) rows.Add(row);

        var totalWidth = rows.Max(x => x.Sum(i => i.Bitmap.Width));
        var totalHeight = rows.Sum(x => x.Max(i => i.Bitmap.Height));
        totalWidth += totalWidth % 2;
        totalHeight += totalHeight % 2;
        var parts = new List<AtlasPartData>();
        Directory.CreateDirectory(rawDir);
        using (var combined = new Bitmap(totalWidth, totalHeight, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(combined))
            {
                graphics.Clear(Color.Transparent);
                var y = 0;
                foreach (var atlasRow in rows)
                {
                    var rowHeight = atlasRow.Max(x => x.Bitmap.Height);
                    var x = 0;
                    foreach (var image in atlasRow)
                    {
                        var top = y + ((rowHeight - image.Bitmap.Height) / 2);
                        graphics.DrawImageUnscaled(image.Bitmap, x, top);
                        parts.Add(new AtlasPartData(image.Name, x, top, image.ContentWidth, image.ContentHeight,
                            (float)x / totalWidth, (float)top / totalHeight,
                            (float)image.ContentWidth / totalWidth, (float)image.ContentHeight / totalHeight));
                        x += image.Bitmap.Width;
                    }
                    y += rowHeight;
                }
            }
            combined.Save(imageFile, ImageFormat.Png);
            var halfWidth = Math.Max(2, totalWidth / 2 + ((totalWidth / 2) % 2));
            var halfHeight = Math.Max(2, totalHeight / 2 + ((totalHeight / 2) % 2));
            using var half = new Bitmap(halfWidth, halfHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(half))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(combined, 0, 0, halfWidth, halfHeight);
            }
            half.Save(image1080File, ImageFormat.Png);
        }

        var mappers = new CArray<inkTextureAtlasMapper>();
        foreach (var part in parts)
            mappers.Add(new inkTextureAtlasMapper
            {
                PartName = part.Name,
                ClippingRectInPixels = new Rect
                {
                    Left = part.X, Top = part.Y,
                    Right = part.X + part.Width, Bottom = part.Y + part.Height,
                },
                ClippingRectInUVCoords = new RectF
                {
                    Left = part.U, Top = part.V,
                    Right = part.U + part.UWidth, Bottom = part.V + part.VHeight,
                },
            });
        var depotDir = depotPath.Replace('/', '\\').Trim('\\');
        var atlas = new inkTextureAtlas
        {
            ActiveTexture = WolvenKit.RED4.Types.Enums.inkTextureType.StaticTexture,
            IsSingleTextureMode = true,
            TextureResolution = WolvenKit.RED4.Types.Enums.inkETextureResolution.UltraHD_3840_2160,
        };
        atlas.Slots[0].Texture = new CResourceAsyncReference<CBitmapTexture>(
            Path.Combine(depotDir, atlasName + ".xbm"), InternalEnums.EImportFlags.Soft);
        atlas.Slots[0].Parts = mappers;
        atlas.Slots[1].Texture = new CResourceAsyncReference<CBitmapTexture>(
            Path.Combine(depotDir, atlasName + "_1080.xbm"), InternalEnums.EImportFlags.Soft);
        atlas.Slots[1].Parts = mappers;
        Directory.CreateDirectory(modDir);
        using var fs = new FileStream(atlasFile, FileMode.Create, FileAccess.ReadWrite);
        using var writer = new CR2WWriter(fs) { LoggerService = logger };
        writer.WriteFile(new CR2WFile { RootChunk = atlas });
        logger.Success($"Generated InkAtlas with {parts.Count} part(s): {atlasFile}");
        logger.Info($"Atlas image: {imageFile}");
        logger.Info($"1080 atlas image: {image1080File}");
        return 0;
    }
    catch (Exception ex)
    {
        logger.Error($"generate-inkatlas failed: {ex.Message}");
        return -1;
    }
    finally
    {
        foreach (var image in images) image.Bitmap.Dispose();
    }
}

static (int Width, int Height) ScaleToFit(int width, int height, int maxWidth, int maxHeight)
{
    if (maxWidth <= 0 && maxHeight <= 0) return (width, height);
    var widthScale = maxWidth > 0 ? (double)maxWidth / width : double.MaxValue;
    var heightScale = maxHeight > 0 ? (double)maxHeight / height : double.MaxValue;
    var scale = Math.Min(widthScale, heightScale);
    return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
}

static string EscapeCsv(string v)
{
    if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    return v;
}

// Imports raw files with explicit XBM/glTF settings. Mirrors ConsoleFunctions.ImportTask
// while keeping every supplied option deterministic instead of inheriting Modkit defaults.
static async Task<int> ImportConfigured(IServiceProvider provider, CapturingLoggerService logger,
    FileSystemInfo[] paths, DirectoryInfo outDir, bool keep, Dictionary<string, string?> options)
{
    if (paths.Length == 0) { logger.Error("import --configured: no input path"); return -1; }
    using var scope = provider.CreateScope();
    var mt = scope.ServiceProvider.GetRequiredService<IModTools>();
    var common = new CommonImportArgs { Keep = keep };
    var xbm = new XbmImportArgs { Keep = keep };
    var gltf = new GltfImportArgs { Keep = keep };

    bool SetBool(string key, Action<bool> setter)
    {
        var raw = Opt(options, key);
        if (raw is null) return true;
        if (!bool.TryParse(raw, out var value))
        {
            logger.Error($"{key}: expected true or false, got '{raw}'");
            return false;
        }
        setter(value);
        return true;
    }

    bool SetEnum<T>(string key, Action<T> setter) where T : struct, Enum
    {
        var raw = Opt(options, key);
        if (raw is null) return true;
        if (!Enum.TryParse<T>(raw, true, out var value))
        {
            logger.Error($"{key}: unknown {typeof(T).Name} value '{raw}'");
            return false;
        }
        setter(value);
        return true;
    }

    if (!SetEnum<WolvenKit.RED4.Types.Enums.GpuWrapApieTextureGroup>("--texture-group", v => xbm.TextureGroup = v)
        || !SetEnum<WolvenKit.RED4.Types.Enums.ETextureRawFormat>("--raw-format", v => xbm.RawFormat = v)
        || !SetEnum<WolvenKit.RED4.Types.Enums.ETextureCompression>("--compression", v => xbm.Compression = v)
        || !SetBool("--generate-mipmaps", v => xbm.GenerateMipMaps = v)
        || !SetBool("--streamable", v => xbm.IsStreamable = v)
        || !SetBool("--premultiply-alpha", v => xbm.PremultiplyAlpha = v)
        || !SetBool("--gamma", v => xbm.IsGamma = v)
        || !SetBool("--garment-support", v => gltf.ImportGarmentSupport = v)
        || !SetBool("--ignore-garment-uv", v => gltf.IgnoreGarmentSupportUVParam = v)
        || !SetBool("--override-mesh-name", v => gltf.OverrideMeshNameWithNodeName = v)
        || !SetBool("--import-materials", v => gltf.ImportMaterials = v)
        || !SetBool("--material-only", v => gltf.ImportMaterialOnly = v)
        || !SetBool("--fill-empty", v => gltf.FillEmpty = v)
        || !SetBool("--force-compressed", v => gltf.ForceCompressedEncoding = v)
        || !SetBool("--strip-local-transform", v => gltf.AdditiveStripLocalTransform = v)
        || !SetEnum<SharpGLTF.Validation.ValidationMode>("--validation-mode", v => gltf.ValidationMode = v))
        return -1;

    var imports = new List<(FileInfo File, DirectoryInfo BaseDir)>();
    foreach (var path in paths)
    {
        if (path is FileInfo file && file.Exists)
            imports.Add((file, file.Directory ?? new DirectoryInfo(".")));
        else if (path is DirectoryInfo dir && dir.Exists)
            imports.AddRange(dir.EnumerateFiles("*", SearchOption.AllDirectories).Select(file => (file, dir)));
        else
            logger.Warning($"import --configured skips missing path: {path.FullName}");
    }

    int errors = 0;
    foreach (var (file, baseDir) in imports)
    {
        var relative = Path.GetRelativePath(baseDir.FullName, file.FullName);
        var rel = new RedRelativePath(baseDir, relative);
        var args = new GlobalImportArgs()
            .Register(common, xbm, gltf, new MlmaskImportArgs { Keep = keep });
        if (await mt.Import(rel, args, outDir))
            logger.Success($"Successfully imported (configured) {relative}");
        else { logger.Error($"Failed to import {file.Name}"); errors++; }
    }
    if (imports.Count == 0) return -1;
    return errors > 0 ? 1 : 0;
}

// Renders a TweakDB flat value as a TweakXL-flavoured token for the tweakdb-clone inventory:
// CName/TweakDBID as their resolved identifier, floats with InvariantCulture (the engine values
// use '.'; the default ToString() would emit ',' on a fr-FR host), arrays as [a, b], Vector3 as
// { x:, y:, z: }. Unhandled types (LocKey wrapper, resource refs) fall back to a <TypeName> tag —
// harmless because these are emitted as YAML comments, never active keys.
static string RenderTweakFlatValue(WolvenKit.RED4.Types.IRedType? v)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    switch (v)
    {
        case null: return "null";
        case WolvenKit.RED4.Types.CString s: { var str = (string?)s ?? ""; return str.Length == 0 ? "\"\"" : str; }
        case WolvenKit.RED4.Types.CName n: { var str = (string?)n; return string.IsNullOrEmpty(str) ? "None" : str; }
        case WolvenKit.RED4.Types.CBool b: return (bool)b ? "true" : "false";
        case WolvenKit.RED4.Types.CFloat f: return ((float)f).ToString("0.################", inv);
        case WolvenKit.RED4.Types.CDouble d: return ((double)d).ToString("0.################", inv);
        case WolvenKit.RED4.Types.CInt32 i: return ((int)i).ToString(inv);
        case WolvenKit.RED4.Types.CUInt32 u: return ((uint)u).ToString(inv);
        case WolvenKit.RED4.Types.CInt64 i64: return ((long)i64).ToString(inv);
        case WolvenKit.RED4.Types.CUInt64 u64: return ((ulong)u64).ToString(inv);
        case WolvenKit.RED4.Types.CInt16 i16: return ((short)i16).ToString(inv);
        case WolvenKit.RED4.Types.CUInt16 u16: return ((ushort)u16).ToString(inv);
        case WolvenKit.RED4.Types.CInt8 i8: return ((sbyte)i8).ToString(inv);
        case WolvenKit.RED4.Types.CUInt8 u8: return ((byte)u8).ToString(inv);
        case WolvenKit.RED4.Types.TweakDBID id: { var t = id.GetResolvedText(); return string.IsNullOrEmpty(t) ? "null" : t; }
        case WolvenKit.RED4.Types.Vector3 vec:
            return $"{{ x: {RenderTweakFlatValue(vec.X)}, y: {RenderTweakFlatValue(vec.Y)}, z: {RenderTweakFlatValue(vec.Z)} }}";
        case WolvenKit.RED4.Types.IRedArray arr:
        {
            var items = new List<string>();
            foreach (var el in arr) items.Add(RenderTweakFlatValue(el as WolvenKit.RED4.Types.IRedType));
            return "[" + string.Join(", ", items) + "]";
        }
        default:
            return $"<{v.GetType().Name}>";
    }
}

// ────────────────────────────────────────────────────────────────────────
// TweakXL — structured editing of .tweak files (YAML).
// ────────────────────────────────────────────────────────────────────────

static async Task<int> TweakRead(CapturingLoggerService logger, string tweakFile, string outputJsonFile)
{
    if (!File.Exists(tweakFile))
    {
        logger.Error($".tweak file not found: {tweakFile}");
        return -1;
    }
    var yaml = await File.ReadAllTextAsync(tweakFile);
    var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
    object? parsed;
    try { parsed = deserializer.Deserialize<object?>(yaml); }
    catch (Exception ex)
    {
        logger.Error($"invalid YAML in {tweakFile}: {ex.Message}");
        return -1;
    }
    var normalized = NormalizeYamlNode(parsed);
    Directory.CreateDirectory(Path.GetDirectoryName(outputJsonFile) ?? ".");
    await File.WriteAllTextAsync(outputJsonFile,
        JsonSerializer.Serialize(normalized,
            new JsonSerializerOptions { WriteIndented = true }));
    logger.Info($"tweak read OK: {tweakFile} -> {outputJsonFile} ({new FileInfo(outputJsonFile).Length} B)");
    return 0;
}

static async Task<int> TweakWrite(CapturingLoggerService logger, string jsonFile, string outputTweakFile)
{
    if (!File.Exists(jsonFile))
    {
        logger.Error($"JSON file not found: {jsonFile}");
        return -1;
    }
    var jsonText = await File.ReadAllTextAsync(jsonFile);
    using var doc = JsonDocument.Parse(jsonText);
    var dict = JsonElementToObject(doc.RootElement);
    var serializer = new YamlDotNet.Serialization.SerializerBuilder().Build();
    Directory.CreateDirectory(Path.GetDirectoryName(outputTweakFile) ?? ".");
    await File.WriteAllTextAsync(outputTweakFile, serializer.Serialize(dict));
    logger.Info($"tweak write OK: {jsonFile} -> {outputTweakFile} " +
                $"({new FileInfo(outputTweakFile).Length} B)");
    return 0;
}

static async Task<int> TweakValidate(IServiceProvider provider, CapturingLoggerService logger,
    string tweakFile, string tweakdbBin)
{
    if (!File.Exists(tweakFile)) { logger.Error($".tweak file not found: {tweakFile}"); return -1; }
    if (!File.Exists(tweakdbBin)) { logger.Error($"tweakdb.bin not found: {tweakdbBin}"); return -1; }

    var tdb = provider.GetRequiredService<ITweakDBService>();
    await EnsureTweakDbAsync(tdb, tweakdbBin);
    if (!tdb.IsLoaded) { logger.Error($"failed to load TweakDB: {tweakdbBin}"); return -1; }

    var yaml = await File.ReadAllTextAsync(tweakFile);
    var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
    object? parsed;
    try { parsed = deserializer.Deserialize<object?>(yaml); }
    catch (Exception ex)
    {
        logger.Error($"invalid YAML: {ex.Message}"); return -1;
    }

    if (parsed is not Dictionary<object, object> root)
    {
        logger.Error("The root of the .tweak must be a YAML mapping (key: value).");
        return -1;
    }

    int total = 0, ok = 0, instances = 0, missing = 0;
    foreach (var kvp in root)
    {
        var key = kvp.Key?.ToString() ?? "";
        if (string.IsNullOrEmpty(key)) continue;
        total++;
        // A new/derived record is declared with $base (clone an existing record) or $type
        // (a fresh record of a given type) — TweakXL's attributes. ($instanceOf is tolerated
        // for legacy hand-written tweaks but is not a real TweakXL key.)
        var isNewRecord = kvp.Value is Dictionary<object, object> body &&
                          body.Keys.Any(k => k?.ToString() is "$base" or "$type" or "$instanceOf");
        if (isNewRecord)
        {
            instances++;
            logger.Info($"  + {key} ($base/$type: new record)");
            continue;
        }
        // Hash the name via the official TweakDBID function, then check
        // whether the TweakDB knows this identifier.
        var hash = WolvenKit.RED4.Types.TweakDBID.CalculateHash(key);
        var resolved = tdb.GetString(hash);
        if (!string.IsNullOrEmpty(resolved))
        {
            ok++;
            logger.Info($"  + {key} (known)");
        }
        else
        {
            missing++;
            logger.Warning(
                $"  - {key}: unknown in TweakDB (add $base or $type if it's a new record)");
        }
    }
    logger.Info($"tweak validate: {total} key(s); {ok} OK + {instances} new record(s) + {missing} unknown");
    return missing > 0 ? 1 : 0;
}

/// <summary>Converts the raw tree returned by YamlDotNet (a mix of
/// Dictionary&lt;object,object&gt;, List&lt;object&gt; and scalars) into a
/// 100% JSON-friendly tree (Dictionary&lt;string,object?&gt; with normalized keys
/// and typed scalar values).</summary>
static object? NormalizeYamlNode(object? node)
{
    switch (node)
    {
        case null:
            return null;
        case Dictionary<object, object> d:
            var dict = new Dictionary<string, object?>();
            foreach (var kvp in d)
                dict[kvp.Key?.ToString() ?? ""] = NormalizeYamlNode(kvp.Value);
            return dict;
        case List<object> list:
            return list.Select(NormalizeYamlNode).ToList();
        case string s:
            // YamlDotNet returns every scalar as a string; we try to type it.
            if (long.TryParse(s, out var l)) return l;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dbl)) return dbl;
            if (bool.TryParse(s, out var b)) return b;
            return s;
        default:
            return node?.ToString();
    }
}

/// <summary>Reverse conversion — JsonElement to the Dictionary/List tree
/// that YamlDotNet can serialize.</summary>
static object? JsonElementToObject(JsonElement el)
{
    switch (el.ValueKind)
    {
        case JsonValueKind.Object:
            var d = new Dictionary<object, object?>();
            foreach (var prop in el.EnumerateObject())
                d[prop.Name] = JsonElementToObject(prop.Value);
            return d;
        case JsonValueKind.Array:
            return el.EnumerateArray().Select(JsonElementToObject).ToList();
        case JsonValueKind.String:
            return el.GetString();
        case JsonValueKind.Number:
            if (el.TryGetInt64(out var i)) return i;
            return el.GetDouble();
        case JsonValueKind.True: return true;
        case JsonValueKind.False: return false;
        default: return null;
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────
static (List<string> pos, Dictionary<string, string?> opts) ParseArgs(string[] argv, int skip)
{
    var boolFlags = DaemonState.BoolFlags;
    var pos = new List<string>();
    var opts = new Dictionary<string, string?>();
    for (var i = skip; i < argv.Length; i++)
    {
        var a = argv[i];
        if (a.StartsWith("--"))
        {
            if (boolFlags.Contains(a) || i + 1 >= argv.Length)
            {
                opts[a] = null;
            }
            else
            {
                opts[a] = argv[++i];
            }
        }
        else
        {
            pos.Add(a);
        }
    }
    return (pos, opts);
}

static string? Opt(Dictionary<string, string?> o, string key)
    => o.TryGetValue(key, out var v) ? v : null;

static DirectoryInfo? Dir(Dictionary<string, string?> o, string key)
    => o.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? new DirectoryInfo(v) : null;

static FileSystemInfo Fs(string p)
    => Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);

/// <summary>Resolves the game executable from a path: if already a .exe,
/// returns it; otherwise assumes the install root and composes
/// bin/x64/Cyberpunk2077.exe (expected by ArchiveManager.LoadGameArchives).</summary>
static FileInfo ResolveGameExe(string path)
    => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? new FileInfo(path)
        : new FileInfo(Path.Combine(path, "bin", "x64", "Cyberpunk2077.exe"));

// TweakDBService is a singleton kept warm (its TweakDB lives in a static
// field). Without path tracking, a first load would "stick": querying
// ANOTHER tweakdb.bin returned the data from the first one. So we memoize the
// canonical loaded path (DaemonState) and reload when it changes — or when
// the file itself has been modified (a mod regenerating tweakdb.bin): without the mtime,
// the daemon would serve stale data.
static async Task EnsureTweakDbAsync(ITweakDBService tdb, string path)
{
    var canon = Path.GetFullPath(path);
    var mtime = File.GetLastWriteTimeUtc(canon);
    if (tdb.IsLoaded
        && string.Equals(DaemonState.LoadedTweakDbPath, canon, StringComparison.OrdinalIgnoreCase)
        && DaemonState.LoadedTweakDbMtime == mtime)
        return;
    await tdb.LoadDB(canon);
    if (tdb.IsLoaded)
    {
        DaemonState.LoadedTweakDbPath = canon;
        DaemonState.LoadedTweakDbMtime = mtime;
        // The per-record index belongs to the old database: it will be rebuilt
        // lazily on the next tweakdb-describe.
        DaemonState.IndexedTweakDbPath = null;
        DaemonState.RecordsByName = null;
        DaemonState.FlatsByRecord = null;
    }
}

// TweakDB index by record, built lazily (one O(N) pass over the flats
// on the first tweakdb-describe, then O(1) lookups). Without it, each describe
// re-scanned ALL flats (~500 ms on a full tweakdb.bin).
static void EnsureTweakDbIndex()
{
    if (DaemonState.IndexedTweakDbPath is not null
        && string.Equals(DaemonState.IndexedTweakDbPath, DaemonState.LoadedTweakDbPath,
                         StringComparison.OrdinalIgnoreCase))
        return;

    var records = new Dictionary<string, WolvenKit.RED4.Types.TweakDBID>(StringComparer.Ordinal);
    foreach (var rec in TweakDBService.GetRecords())
    {
        var text = rec.GetResolvedText();
        if (!string.IsNullOrEmpty(text)) records[text] = rec;
    }

    var flats = new Dictionary<string, List<WolvenKit.RED4.Types.TweakDBID>>(StringComparer.Ordinal);
    foreach (var flat in TweakDBService.GetFlats())
    {
        var text = flat.GetResolvedText();
        if (string.IsNullOrEmpty(text)) continue;
        var idx = text.LastIndexOf('.');
        if (idx <= 0) continue;
        var recordId = text[..idx];
        if (!flats.TryGetValue(recordId, out var list))
            flats[recordId] = list = new List<WolvenKit.RED4.Types.TweakDBID>();
        list.Add(flat);
    }

    DaemonState.RecordsByName = records;
    DaemonState.FlatsByRecord = flats;
    DaemonState.IndexedTweakDbPath = DaemonState.LoadedTweakDbPath;
}

// Returns a game ArchiveManager, reusing the cached one when the install is
// unchanged (key = exe path + content-dir mtime). LoadGameArchives re-enumerates
// hundreds of base archives, so caching it removes the worst repeated scan on the
// --game export path. Safe because execLock serializes the verbs that use it.
static IArchiveManager LoadGameArchivesCached(IServiceProvider provider, FileInfo exe)
{
    string? key = null;
    try
    {
        var contentDir = Path.Combine(exe.Directory!.Parent!.Parent!.FullName, "archive", "pc", "content");
        if (Directory.Exists(contentDir))
            key = exe.FullName + "|" + Directory.GetLastWriteTimeUtc(contentDir).Ticks;
    }
    catch { /* unusual layout — fall through to an uncached fresh load */ }

    if (key is not null && DaemonState.GameArchivesKey == key && DaemonState.GameArchives is not null)
        return DaemonState.GameArchives;

    DaemonState.GameArchivesScope?.Dispose();
    var scope = provider.CreateScope();
    var am = scope.ServiceProvider.GetRequiredService<IArchiveManager>();
    am.LoadGameArchives(exe);
    DaemonState.GameArchivesScope = scope;
    DaemonState.GameArchives = am;
    DaemonState.GameArchivesKey = key; // null key ⇒ next call reloads (uncached)
    return am;
}

static EUncookExtension? ParseUext(string? s)
    => Enum.TryParse<EUncookExtension>(s, ignoreCase: true, out var e) ? e : null;

static ECookedFileFormat[]? ParseCookedFormats(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => Enum.TryParse<ECookedFileFormat>(x, true, out var parsed) ? parsed : (ECookedFileFormat?)null)
        .Where(x => x.HasValue)
        .Select(x => x!.Value)
        .ToArray();
    return values.Length == 0 ? null : values;
}

static List<uint> ParseUintList(string? s)
{
    var list = new List<uint>();
    if (string.IsNullOrWhiteSpace(s)) return list;
    foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (uint.TryParse(part, out var v)) list.Add(v);
    return list;
}

// ── IPC protocol ────────────────────────────────────────────────────────
// Shared daemon state (top-level statement files don't allow
// static fields at the root level — we group them here).
static class DaemonState
{
    // Canonical path + mtime of the last loaded tweakdb.bin (the service is
    // a warm singleton; we reload when the path OR the file changes).
    public static string? LoadedTweakDbPath;
    public static DateTime LoadedTweakDbMtime;

    // Per-record index (built lazily by EnsureTweakDbIndex, invalidated
    // when the database is reloaded).
    public static string? IndexedTweakDbPath;
    public static Dictionary<string, WolvenKit.RED4.Types.TweakDBID>? RecordsByName;
    public static Dictionary<string, List<WolvenKit.RED4.Types.TweakDBID>>? FlatsByRecord;

    // Cached game ArchiveManager (P3): LoadGameArchives re-scans archive/pc/content
    // (hundreds of base archives) on every --game export. Cached keyed by exe +
    // content-dir mtime. execLock serializes execution so one shared instance is
    // safe; the lock-free read-only fast paths never touch it.
    public static string? GameArchivesKey;
    public static IServiceScope? GameArchivesScope;
    public static IArchiveManager? GameArchives;

    // Boolean flags (no value) — immutable, shared across requests.
    public static readonly HashSet<string> BoolFlags = new()
    {
        "--list", "--diff", "--wem", "--structured", "--keep", "--all",
        "--unbundle", "--serialize", "--mesh-export-lod-filter",
        "--opus-export-all", "--debug-decompress",
        "--mesh-export-experimental-merged-export", "--configured", "--overwrite",
    };
}

sealed record DaemonRequest(int id, string[] argv);
sealed record DaemonResponse(int id, int exit, string output);
sealed record DaemonProgress(int id, string progress);
sealed record AtlasSource(string Name, Bitmap Bitmap, int ContentWidth, int ContentHeight);
sealed record AtlasPartData(string Name, int X, int Y, int Width, int Height,
    float U, float V, float UWidth, float VHeight);

// ════════════════════════════════════════════════════════════════════════
// Shims — minimal implementations of WolvenKit's service interfaces.
// ════════════════════════════════════════════════════════════════════════

/// <summary>ILoggerService that accumulates output in a drainable buffer.</summary>
sealed class CapturingLoggerService : ILoggerService
{
    private readonly StringBuilder _sb = new();

    public LoggerVerbosity LoggerVerbosity { get; private set; } = LoggerVerbosity.Normal;
    public void SetLoggerVerbosity(LoggerVerbosity verbosity) => LoggerVerbosity = verbosity;

    public void Info(string s) => Append("Information", s);
    public void Warning(string s) => Append("Warning", s);
    public void Error(string msg) => Append("Error", msg);
    public void Success(string msg) => Append("Success", msg);
    public void Debug(string msg) => Append("Debug", msg);
    public void Error(Exception exception) => Append("Error", exception.Message);

    /// <summary>Progress relay to the IPC client. Set per request
    /// (under execLock); the log lines of the current execution are relayed to it.</summary>
    public Action<string>? ProgressSink;

    private void Append(string level, string s)
    {
        // Same format as the cp77tools logger: "[ 0: Level ] - message".
        // The MCP server's Format() relies on the ": Success" / ": Error" markers.
        lock (_sb) _sb.AppendLine($"[ 0: {level} ] - {s}");
        ProgressSink?.Invoke(s);
    }

    /// <summary>"Percentage" progress (WolvenKit's IProgressService):
    /// relayed to the client without polluting the output buffer.</summary>
    public void EmitPercent(double value)
        => ProgressSink?.Invoke(value <= 1.0 ? $"{value * 100:F0} %" : $"{value:F0}");

    /// <summary>Appends raw text (captured Console output) to the same buffer.</summary>
    public void AppendRaw(string s)
    {
        lock (_sb) _sb.Append(s);
    }

    /// <summary>Returns the accumulated output and clears the buffer.</summary>
    public string Drain()
    {
        lock (_sb)
        {
            var r = _sb.ToString();
            _sb.Clear();
            return r;
        }
    }
}

/// <summary>
/// TextWriter that drains Console.Out writes to the capturing logger.
/// Indispensable: some WolvenKit tasks (archive listing…) write
/// their result via Console.WriteLine rather than via ILoggerService.
/// </summary>
sealed class CapturingTextWriter : TextWriter
{
    private readonly CapturingLoggerService _sink;
    public CapturingTextWriter(CapturingLoggerService sink) => _sink = sink;

    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) => _sink.AppendRaw(value.ToString());
    public override void Write(string? value) { if (value is not null) _sink.AppendRaw(value); }
    public override void WriteLine(string? value) => _sink.AppendRaw((value ?? "") + Environment.NewLine);

    // Batched overrides: without these, TextWriter's base implementation routes
    // buffer/span writes through Write(char) one character at a time — a string
    // allocation and a lock per character. A large archive listing went through
    // this path; one AppendRaw per chunk instead removes the alloc/lock storm.
    public override void Write(char[] buffer, int index, int count)
        => _sink.AppendRaw(new string(buffer, index, count));
    public override void Write(ReadOnlySpan<char> buffer)
        => _sink.AppendRaw(new string(buffer));
}

#pragma warning disable CS0067 // events never raised — minimal relay
/// <summary>IProgressService that relays WolvenKit percentages to the logger's
/// progress sink (→ {"id":N,"progress":"42 %"} messages on the client side).</summary>
sealed class BridgingProgressService : IProgressService<double>
{
    private readonly CapturingLoggerService _logger;
    public BridgingProgressService(CapturingLoggerService logger) => _logger = logger;

    public event EventHandler<double>? ProgressChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsIndeterminate { get; set; }
    public EStatus Status { get; set; }

    public void Completed() { }
    public void Report(double value) => _logger.EmitPercent(value);
}
#pragma warning restore CS0067
