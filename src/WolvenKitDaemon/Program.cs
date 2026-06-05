using System.ComponentModel;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using CP77Tools.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WolvenKit.Common;
using WolvenKit.Common.Interfaces;
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

// ════════════════════════════════════════════════════════════════════════
// WolvenKitDaemon — hôte persistant des bibliothèques WolvenKit.
//
// Charge HashService UNE seule fois (~6 s au démarrage), puis traite des
// requêtes successives sur stdin/stdout sans payer ce cold-start.
//
// Protocole (JSON, une ligne par message) :
//   requête  : {"id":N,"argv":["unbundle","/x.archive","--outpath","/out"]}
//   réponse  : {"id":N,"exit":0,"output":"[ Information ] ..."}
//   au prêt  : {"ready":true}
//
// Codes de sortie (exit) : 0 = succès ; -1 = erreur d'argument ou de chargement ;
// 1 = résultat vide / non trouvé (ex. loc-resolve --key absent, tweak validate avec
// clés inconnues, opus-import sans encodage). id=0 est réservé aux erreurs de
// désérialisation. Les réponses peuvent arriver dans le désordre (pipelining) alors
// que l'exécution est sérialisée (execLock) ; stdout est réservé au protocole.
//
// Verbes spéciaux (hors ConsoleFunctions) :
//   loc-resolve <racineJeu|.exe> [--lang en_us] (--key <hash|cléSecondaire> | --all)
//     → LocKey → texte localisé (variantes M/F). --all liste les 100 premières entrées.
//       Précédence : si --all et --key sont fournis, --all l'emporte (--key ignoré).
//   opus-import <racineJeu|.exe> --wav-dir <dir> --out <dir>
//     → WAV (nommés par hash opus) → .opus repacké (encodeur opusenc embarqué).
// ════════════════════════════════════════════════════════════════════════

// stdout est le canal JSON réservé au protocole : on en garde une référence
// (channel) avant toute redirection.
var channel = Console.Out;

// Certaines tâches WolvenKit (le listing de « archive --list », notamment)
// écrivent leur résultat via Console.WriteLine et non via ILoggerService. On
// redirige donc Console.Out vers le même tampon que le logger capturant — sans
// quoi cette sortie serait perdue et archive_info / find_in_archives / la
// ressource d'archive renverraient du vide.
var logger = new CapturingLoggerService();
Console.SetOut(new CapturingTextWriter(logger));

// Certaines dépendances de WolvenKit (DirectXTexNet, conversion de textures)
// sont livrées comme fichiers de contenu : copiées dans la sortie de build mais
// absentes du deps.json, donc introuvables pour le résolveur .NET par défaut.
// On ajoute un repli qui les charge depuis le dossier de l'application.
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var dll = Path.Combine(AppContext.BaseDirectory, (name.Name ?? "") + ".dll");
    return File.Exists(dll) ? ctx.LoadFromAssemblyPath(dll) : null;
};

Oodle.Load();

var services = new ServiceCollection();
services.AddLogging();

// Implémentations maison (celles de WolvenKit.CLI sont internal).
services.AddSingleton<ILoggerService>(logger);
services.AddSingleton<IProgressService<double>, NoopProgressService>();

// Données de référence — singletons gardés chauds (HashService = le coût ~6 s).
services.AddSingleton<IHashService, HashService>();
services.AddSingleton<ITweakDBService, TweakDBService>();
services.AddSingleton<ILocKeyService, LocKeyService>();
services.AddSingleton<IHookService, HookService>();

// État par opération — scoped : ArchiveManager accumule les archives chargées,
// il DOIT être neuf à chaque requête (sinon fuite d'état entre appels).
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

// Préchauffage : construit HashService maintenant (~6 s payés une fois).
using (var warm = provider.CreateScope())
{
    _ = warm.ServiceProvider.GetRequiredService<ConsoleFunctions>();
}
channel.WriteLine("""{"ready":true}""");
channel.Flush();

// Pipelining IPC : la lecture des requêtes ne bloque pas sur l'exécution. Plusieurs
// requêtes peuvent être en vol, mais l'exécution reste sérialisée par execLock car
// les bibliothèques WolvenKit (logger, archive manager, Console.Out captured) ne
// sont pas thread-safe pour l'exécution. L'overlap utile : décoder la requête N+1
// pendant que N s'exécute, et que le client puisse envoyer en pipeline sans attendre.
var execLock = new SemaphoreSlim(1, 1);
var writeLock = new SemaphoreSlim(1, 1);

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
            exit = await Dispatch(provider, logger, req.argv);
            output = logger.Drain().Trim();
        }
        finally { execLock.Release(); }
    }
    catch (Exception ex)
    {
        exit = -1;
        // Drainer le log accumulé avant l'exception (souvent le vrai diagnostic :
        // progression, étapes) au lieu de ne renvoyer que le message nu.
        var partial = logger.Drain().Trim();
        output = (partial.Length > 0 ? partial + "\n" : "") +
                 "[ 0: Error ] - Erreur daemon : " + ex.Message;
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
                new DaemonResponse(0, -1, "Requête JSON invalide : " + ex.Message), json));
            await channel.FlushAsync();
        }
        finally { writeLock.Release(); }
        continue;
    }
    if (req is null) continue;

    pending.Add(Task.Run(() => HandleRequest(req)));

    // Empêche la liste de pending de grossir indéfiniment : on draine ce qui est terminé.
    pending.RemoveAll(t => t.IsCompleted);
}

await Task.WhenAll(pending);
return 0;

// ════════════════════════════════════════════════════════════════════════
// Dispatch — verbe + argv → méthode de ConsoleFunctions.
// ════════════════════════════════════════════════════════════════════════
static async Task<int> Dispatch(IServiceProvider provider, CapturingLoggerService logger, string[] argv)
{
    if (argv.Length == 0)
    {
        logger.Error("argv vide");
        return -1;
    }

    var verb = argv[0];

    if (verb is "--version" or "version")
    {
        var v = typeof(ConsoleFunctions).Assembly.GetName().Version?.ToString() ?? "inconnue";
        logger.Info($"WolvenKitDaemon — WolvenKit.Modkit {v}");
        return 0;
    }

    // loc-resolve : LocKey (hash ou clé) → texte localisé via LocKeyService.
    // Charge les archives du jeu dans un ArchiveManager neuf (scope dédié), puis
    // une langue d'on-screens, et résout la clé. --all liste les premières entrées.
    if (verb == "loc-resolve")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("loc-resolve : chemin du jeu (racine ou .exe) manquant"); return -1; }
        var exe = ResolveGameExe(pos[0]);
        if (!exe.Exists) { logger.Error($"exécutable du jeu introuvable : {exe.FullName}"); return -1; }
        var lang = Enum.TryParse<EGameLanguage>(Opt(o, "--lang"), ignoreCase: true, out var lg)
            ? lg : EGameLanguage.en_us;

        using var locScope = provider.CreateScope();
        var sp = locScope.ServiceProvider;
        var am = sp.GetRequiredService<IArchiveManager>();
        am.LoadGameArchives(exe);
        var parser = sp.GetRequiredService<Red4ParserService>();
        var loc = new LocKeyService(parser, am) { Language = lang };
        if (!loc.LoadCurrentLanguage())
        {
            logger.Error($"échec du chargement de la langue {lang} (on-screens introuvables ?)");
            return -1;
        }

        if (o.ContainsKey("--all"))
        {
            var entries = loc.GetEntries().ToList();
            logger.Info($"loc-resolve : {entries.Count} entrée(s) pour {lang} (100 premières)");
            foreach (var e in entries.Take(100))
                logger.Info($"{e.PrimaryKey} | {e.SecondaryKey} = {e.MaleVariant}");
            return 0;
        }

        var key = Opt(o, "--key");
        if (string.IsNullOrEmpty(key)) { logger.Error("loc-resolve : --key requis (hash uint64 ou clé secondaire)"); return -1; }
        var entry = ulong.TryParse(key, out var kh) ? loc.GetEntry(kh) : loc.GetEntry(key);
        if (entry is null) { logger.Info($"loc-resolve : aucune entrée pour '{key}' ({lang})"); return 1; }
        logger.Info($"loc-resolve {lang} '{key}' : male=\"{entry.MaleVariant}\" " +
                    $"female=\"{entry.FemaleVariant}\" secondaryKey=\"{entry.SecondaryKey}\"");
        return 0;
    }

    // opus-import : importe des .wav (nommés par hash opus) → .opus repackés dans
    // un dossier de mod, via OpusTools.ImportWavs (encodeur opusenc embarqué).
    if (verb == "opus-import")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("opus-import : chemin du jeu (racine ou .exe) manquant"); return -1; }
        var exe = ResolveGameExe(pos[0]);
        if (!exe.Exists) { logger.Error($"exécutable du jeu introuvable : {exe.FullName}"); return -1; }
        var wavDir = Opt(o, "--wav-dir");
        var outDir = Opt(o, "--out");
        if (string.IsNullOrEmpty(wavDir) || !Directory.Exists(wavDir)) { logger.Error($"--wav-dir introuvable : {wavDir}"); return -1; }
        if (string.IsNullOrEmpty(outDir)) { logger.Error("opus-import : --out requis"); return -1; }
        var wavs = Directory.GetFiles(wavDir, "*.wav").ToList();
        if (wavs.Count == 0) { logger.Error($"aucun .wav dans {wavDir} (les noms doivent être des hashes opus, ex. 123456.wav)"); return -1; }
        Directory.CreateDirectory(outDir);

        using var opusScope = provider.CreateScope();
        var am = opusScope.ServiceProvider.GetRequiredService<IArchiveManager>();
        am.LoadGameArchives(exe);
        var ok = OpusTools.ImportWavs(am, wavs, new DirectoryInfo(wavDir), new DirectoryInfo(outDir));
        logger.Info($"opus-import : {wavs.Count} wav → {outDir} ({(ok ? "OK" : "échec")})");
        return ok ? 0 : 1;
    }

    // export-entity : exporte une apparence d'entité (.ent) en glTF via IModTools.
    // Charge les archives du jeu (les apparences référencent meshes/matériaux en base).
    if (verb == "export-entity")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("export-entity : fichier .ent manquant"); return -1; }
        var entFile = pos[0];
        var outFile = Opt(o, "--out");
        var appearance = Opt(o, "--appearance") ?? "";
        var gameArg = Opt(o, "--game");
        if (string.IsNullOrEmpty(outFile)) { logger.Error("export-entity : --out requis"); return -1; }
        if (!File.Exists(entFile)) { logger.Error($".ent introuvable : {entFile}"); return -1; }

        using var entScope = provider.CreateScope();
        var sp = entScope.ServiceProvider;
        var am = sp.GetRequiredService<IArchiveManager>();
        if (!string.IsNullOrEmpty(gameArg))
        {
            var exe = ResolveGameExe(gameArg);
            if (!exe.Exists) { logger.Error($"exécutable du jeu introuvable : {exe.FullName}"); return -1; }
            am.LoadGameArchives(exe);
        }
        var parser = sp.GetRequiredService<Red4ParserService>();
        var mt = sp.GetRequiredService<IModTools>();
        using var efs = File.OpenRead(entFile);
        if (!parser.TryReadRed4File(efs, out var entCr2w) || entCr2w is null)
        { logger.Error("lecture du CR2W .ent échouée"); return -1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        var ok = mt.ExportEntity(entCr2w, appearance, new FileInfo(outFile));
        logger.Info($"export-entity : {entFile} [{appearance}] → {outFile} ({(ok ? "OK" : "échec")})");
        return ok ? 0 : 1;
    }

    // export-materials : exporte les matériaux d'un .mesh en JSON+textures via IModTools.
    if (verb == "export-materials")
    {
        var (pos, o) = ParseArgs(argv, 1);
        if (pos.Count == 0) { logger.Error("export-materials : fichier .mesh manquant"); return -1; }
        var meshFile = pos[0];
        var outFile = Opt(o, "--out");
        var gameArg = Opt(o, "--game");
        if (string.IsNullOrEmpty(outFile)) { logger.Error("export-materials : --out requis"); return -1; }
        if (!File.Exists(meshFile)) { logger.Error($".mesh introuvable : {meshFile}"); return -1; }

        using var matScope = provider.CreateScope();
        var sp = matScope.ServiceProvider;
        var am = sp.GetRequiredService<IArchiveManager>();
        if (!string.IsNullOrEmpty(gameArg))
        {
            var exe = ResolveGameExe(gameArg);
            if (!exe.Exists) { logger.Error($"exécutable du jeu introuvable : {exe.FullName}"); return -1; }
            am.LoadGameArchives(exe);
        }
        var parser = sp.GetRequiredService<Red4ParserService>();
        var mt = sp.GetRequiredService<IModTools>();
        using var mfs = File.OpenRead(meshFile);
        if (!parser.TryReadRed4File(mfs, out var meshCr2w) || meshCr2w is null)
        { logger.Error("lecture du CR2W .mesh échouée"); return -1; }
        var outFi = new FileInfo(outFile);
        Directory.CreateDirectory(outFi.DirectoryName!);
        // ExportMaterials a besoin d'un dépôt de matériaux (où écrire/résoudre les
        // textures) ; sans MaterialRepo l'export échoue. On utilise le dossier de sortie.
        var mea = new MeshExportArgs
        {
            MaterialRepo = outFi.DirectoryName,
            MaterialUncookExtension = EUncookExtension.png,
            withMaterials = true,
        };
        var ok = mt.ExportMaterials(meshCr2w, outFi, mea);
        logger.Info($"export-materials : {meshFile} → {outFile} ({(ok ? "OK" : "échec")})");
        return ok ? 0 : 1;
    }

    // resolve-hash : recherche inverse hash → chemin, via HashService (déjà chaud).
    if (verb == "resolve-hash")
    {
        var hashes = provider.GetRequiredService<IHashService>();
        foreach (var arg in argv[1..])
        {
            if (ulong.TryParse(arg, out var h))
            {
                var path = hashes.Get(h);
                logger.Info(path is not null ? $"{h} = {path}" : $"{h} = (hash inconnu)");
            }
            else
            {
                logger.Error($"hash invalide (entier non signé attendu) : {arg}");
            }
        }
        return 0;
    }

    // tweakdb-resolve : hash d'identifiant TweakDB → nom (TweakDBIDPool, déjà chaud).
    if (verb == "tweakdb-resolve")
    {
        var tdb = provider.GetRequiredService<ITweakDBService>();
        foreach (var arg in argv[1..])
        {
            if (ulong.TryParse(arg, out var h))
            {
                var name = tdb.GetString(h);
                logger.Info(name is not null ? $"{h} = {name}" : $"{h} = (inconnu)");
            }
            else
            {
                logger.Error($"hash invalide (entier non signé attendu) : {arg}");
            }
        }
        return 0;
    }

    // tweakdb-query : charge une tweakdb.bin et liste records/flats filtrés.
    if (verb == "tweakdb-query")
    {
        var rest = argv[1..];
        if (rest.Length < 1)
        {
            logger.Error("tweakdb-query : chemin de la tweakdb.bin manquant");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"échec du chargement de la TweakDB : {rest[0]}");
            return -1;
        }
        var filter = rest.Length > 1 ? rest[1] : "";

        // Cap dur côté daemon pour éviter de saturer le contexte du LLM ; on
        // peeke un élément au-delà du cap pour détecter la troncature.
        const int cap = 100;
        var allRecords = TweakDBService.GetRecords()
            .Select(r => r.ToString() ?? "")
            .Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s).Take(cap + 1).ToList();
        var recordsTruncated = allRecords.Count > cap;
        var records = recordsTruncated ? allRecords.Take(cap).ToList() : allRecords;
        var allFlats = TweakDBService.GetFlats()
            .Select(flt => flt.ToString() ?? "")
            .Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s).Take(cap + 1).ToList();
        var flatsTruncated = allFlats.Count > cap;
        var flats = flatsTruncated ? allFlats.Take(cap).ToList() : allFlats;
        // Marqueur "+" lu côté MCP pour exposer un champ truncated dans la sortie JSON.
        var advice = recordsTruncated || flatsTruncated ? " — affiner le filtre" : "";
        logger.Info(
            $"{records.Count}{(recordsTruncated ? "+" : "")} record(s), " +
            $"{flats.Count}{(flatsTruncated ? "+" : "")} flat(s) — " +
            $"filtre \"{filter}\" (cap {cap}{advice})");
        foreach (var rec in records) logger.Info("record  " + rec);
        foreach (var flt in flats) logger.Info("flat    " + flt);
        return 0;
    }

    // tweakdb-describe : pour un identifiant TweakDB, liste tous ses flats avec
    // leurs types et valeurs courantes. Indispensable avant write_tweak.
    if (verb == "tweakdb-describe")
    {
        var rest = argv[1..];
        if (rest.Length < 2)
        {
            logger.Error("tweakdb-describe : tweakdbBin et recordId requis");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"échec du chargement TweakDB : {rest[0]}");
            return -1;
        }

        var recordId = rest[1];
        var prefix = recordId + ".";

        // Cherche le record par nom résolu (parcours O(N) — < 100K records).
        WolvenKit.RED4.Types.TweakDBID? recordTdbId = null;
        foreach (var rec in TweakDBService.GetRecords())
        {
            if (rec.GetResolvedText() == recordId)
            {
                recordTdbId = rec;
                break;
            }
        }
        if (recordTdbId is null)
        {
            logger.Warning($"record inconnu dans TweakDB : {recordId}");
            return 0;
        }

        string? recordType = null;
        if (TweakDBService.TryGetType(recordTdbId.Value, out var t))
            recordType = t?.Name;
        logger.Info($"record {recordId} ({recordType ?? "?"})");

        int found = 0;
        foreach (var flat in TweakDBService.GetFlats())
        {
            var text = flat.GetResolvedText();
            if (text is null || !text.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var fieldName = text.Substring(prefix.Length);
            var value = TweakDBService.GetFlat(flat);
            var typeName = value?.GetType().Name ?? "?";
            var valueStr = value?.ToString() ?? "(null)";
            // Tronque les valeurs très longues (arrays massifs).
            if (valueStr.Length > 200) valueStr = valueStr[..200] + "…";
            logger.Info($"  flat  {fieldName} : {typeName} = {valueStr}");
            found++;
        }
        logger.Info($"{found} flat(s) sous {recordId}");
        return 0;
    }

    // tweakdb-extract-localization : extrait tous les flats "displayName" et
    // "localizedDescription" (et variantes) connus de la TweakDB, indexés par
    // recordId. Sortie JSON, prête à servir de base pour un mod de traduction.
    if (verb == "tweakdb-extract-localization")
    {
        var rest = argv[1..];
        if (rest.Length < 2)
        {
            logger.Error("tweakdb-extract-localization : tweakdbBin et outputJson requis");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"échec du chargement TweakDB : {rest[0]}");
            return -1;
        }
        var outputJson = rest[1];
        var filter = rest.Length > 2 ? rest[2] : ""; // ex. "Items." pour limiter

        var localizable = new HashSet<string>(StringComparer.Ordinal)
        {
            "displayName", "localizedDescription", "description",
            "uiName", "name", "menuName", "shortDescription", "longDescription",
        };

        // Index flats par recordId comme dans dump-records.
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
            // Tronque les très longues valeurs (descriptions verbeuses possibles).
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

        logger.Info($"tweakdb-extract-localization : {byRecord.Count} record(s) avec " +
                    $"champs traduisibles → {outputJson}" +
                    (filter.Length > 0 ? $" (filtre: {filter})" : ""));
        return 0;
    }

    // tweakdb-dump-records : exporte tous les records d'un type donné en
    // JSON Lines ou CSV. Indexe d'abord les flats par recordId pour rester en
    // O(records × flats_par_record) au lieu de O(records × N_flats_total).
    if (verb == "tweakdb-dump-records")
    {
        var rest = argv[1..];
        if (rest.Length < 3)
        {
            logger.Error("tweakdb-dump-records : tweakdbBin, recordType, outputFile requis");
            return -1;
        }
        var tdb = provider.GetRequiredService<ITweakDBService>();
        await EnsureTweakDbAsync(tdb, rest[0]);
        if (!tdb.IsLoaded)
        {
            logger.Error($"échec du chargement TweakDB : {rest[0]}");
            return -1;
        }

        var recordType = rest[1];
        var outputFile = rest[2];
        var format = rest.Length > 3 ? rest[3].ToLowerInvariant() : "jsonl";

        // Index flats par recordId (préfixe avant le dernier point).
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

        // Cherche les records du type demandé.
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
            logger.Warning($"Aucun record de type : {recordType}");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile);

        int written = 0;
        if (format == "csv")
        {
            // Collecte des colonnes en deux passes pour superset stable.
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
            // JSON Lines (1 record par ligne).
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

        logger.Info($"dump_records : {recordType} → {outputFile} " +
                    $"({written} ligne(s), format={format})");
        return 0;
    }

    // tweak : édition structurée des fichiers .tweak (TweakXL — YAML).
    if (verb == "tweak")
    {
        var sub = argv.Length > 1 ? argv[1] : "";
        var rest = argv[2..];
        switch (sub)
        {
            case "read":
                if (rest.Length < 2)
                {
                    logger.Error("tweak read : tweakFile et outputJsonFile requis");
                    return -1;
                }
                return await TweakRead(logger, rest[0], rest[1]);
            case "write":
                if (rest.Length < 2)
                {
                    logger.Error("tweak write : jsonFile et outputTweakFile requis");
                    return -1;
                }
                return await TweakWrite(logger, rest[0], rest[1]);
            case "validate":
                if (rest.Length < 2)
                {
                    logger.Error("tweak validate : tweakFile et tweakdbBin requis");
                    return -1;
                }
                return await TweakValidate(provider, logger, rest[0], rest[1]);
            default:
                logger.Error($"tweak : sous-commande inconnue : {sub} (read | write | validate)");
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
                pattern = Opt(o, "--pattern"),
                regex = Opt(o, "--regex"),
            });
        }

        case "uncook":
        {
            var (pos, o) = ParseArgs(argv, 1);
            // UncookTaskOptions a des propriétés init-only — toutes en un seul initialiseur.
            MeshExporterType? met = Enum.TryParse<MeshExporterType>(
                Opt(o, "--mesh-exporter-type"), ignoreCase: true, out var metVal) ? metVal : null;
            MeshExportType? mxt = Enum.TryParse<MeshExportType>(
                Opt(o, "--mesh-export-type"), ignoreCase: true, out var mxtVal) ? mxtVal : null;
            // Audio voix-off : --opus-export-all extrait tous les .opus de l'archive ;
            // --opus-hashes 12345,67890 cible des hashes opus précis (uint32).
            var opusHashes = ParseUintList(Opt(o, "--opus-hashes"));
            return f.UncookTask(pos.Select(Fs).ToArray(), new UncookTaskOptions
            {
                outpath = Dir(o, "--outpath"),
                pattern = Opt(o, "--pattern"),
                regex = Opt(o, "--regex"),
                uext = ParseUext(Opt(o, "--uext")),
                unbundle = o.ContainsKey("--unbundle"),
                serialize = o.ContainsKey("--serialize") ? true : null,
                meshExporterType = met,
                meshExportType = mxt,
                meshExportLodFilter = o.ContainsKey("--mesh-export-lod-filter") ? true : null,
                opusExportAll = o.ContainsKey("--opus-export-all") ? true : null,
                opusHashes = opusHashes,
            });
        }

        case "export":
        {
            var (pos, o) = ParseArgs(argv, 1);
            return f.ExportTask(pos.Select(Fs).ToArray(), new ExportTaskOptions
            {
                outpath = Dir(o, "--outpath"),
                uext = ParseUext(Opt(o, "--uext")),
            });
        }

        case "import":
        {
            var (pos, o) = ParseArgs(argv, 1);
            return await f.ImportTask(pos.Select(Fs).ToArray(),
                Dir(o, "--outpath") ?? new DirectoryInfo("."), o.ContainsKey("--keep"));
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
                logger.Error("oodle : chemins d'entrée/sortie manquants");
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
                logger.Error("wwise : chemins d'entrée/sortie manquants");
                return -1;
            }
            return f.WwiseTask(new FileInfo(pos[0]), new FileInfo(pos[1]), o.ContainsKey("--wem"));
        }

        default:
            logger.Error($"verbe inconnu : {verb}");
            return -1;
    }
}

static string EscapeCsv(string v)
{
    if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    return v;
}

// ────────────────────────────────────────────────────────────────────────
// TweakXL — édition structurée des fichiers .tweak (YAML).
// ────────────────────────────────────────────────────────────────────────

static async Task<int> TweakRead(CapturingLoggerService logger, string tweakFile, string outputJsonFile)
{
    if (!File.Exists(tweakFile))
    {
        logger.Error($"Fichier .tweak introuvable : {tweakFile}");
        return -1;
    }
    var yaml = await File.ReadAllTextAsync(tweakFile);
    var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
    object? parsed;
    try { parsed = deserializer.Deserialize<object?>(yaml); }
    catch (Exception ex)
    {
        logger.Error($"YAML invalide dans {tweakFile} : {ex.Message}");
        return -1;
    }
    var normalized = NormalizeYamlNode(parsed);
    Directory.CreateDirectory(Path.GetDirectoryName(outputJsonFile) ?? ".");
    await File.WriteAllTextAsync(outputJsonFile,
        JsonSerializer.Serialize(normalized,
            new JsonSerializerOptions { WriteIndented = true }));
    logger.Info($"tweak read OK : {tweakFile} -> {outputJsonFile} ({new FileInfo(outputJsonFile).Length} o)");
    return 0;
}

static async Task<int> TweakWrite(CapturingLoggerService logger, string jsonFile, string outputTweakFile)
{
    if (!File.Exists(jsonFile))
    {
        logger.Error($"Fichier JSON introuvable : {jsonFile}");
        return -1;
    }
    var jsonText = await File.ReadAllTextAsync(jsonFile);
    using var doc = JsonDocument.Parse(jsonText);
    var dict = JsonElementToObject(doc.RootElement);
    var serializer = new YamlDotNet.Serialization.SerializerBuilder().Build();
    Directory.CreateDirectory(Path.GetDirectoryName(outputTweakFile) ?? ".");
    await File.WriteAllTextAsync(outputTweakFile, serializer.Serialize(dict));
    logger.Info($"tweak write OK : {jsonFile} -> {outputTweakFile} " +
                $"({new FileInfo(outputTweakFile).Length} o)");
    return 0;
}

static async Task<int> TweakValidate(IServiceProvider provider, CapturingLoggerService logger,
    string tweakFile, string tweakdbBin)
{
    if (!File.Exists(tweakFile)) { logger.Error($"Fichier .tweak introuvable : {tweakFile}"); return -1; }
    if (!File.Exists(tweakdbBin)) { logger.Error($"tweakdb.bin introuvable : {tweakdbBin}"); return -1; }

    var tdb = provider.GetRequiredService<ITweakDBService>();
    await EnsureTweakDbAsync(tdb, tweakdbBin);
    if (!tdb.IsLoaded) { logger.Error($"échec du chargement TweakDB : {tweakdbBin}"); return -1; }

    var yaml = await File.ReadAllTextAsync(tweakFile);
    var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
    object? parsed;
    try { parsed = deserializer.Deserialize<object?>(yaml); }
    catch (Exception ex)
    {
        logger.Error($"YAML invalide : {ex.Message}"); return -1;
    }

    if (parsed is not Dictionary<object, object> root)
    {
        logger.Error("La racine du .tweak doit être un mapping YAML (clé: valeur).");
        return -1;
    }

    int total = 0, ok = 0, instances = 0, missing = 0;
    foreach (var kvp in root)
    {
        var key = kvp.Key?.ToString() ?? "";
        if (string.IsNullOrEmpty(key)) continue;
        total++;
        var hasInstanceOf = kvp.Value is Dictionary<object, object> body &&
                            body.Keys.Any(k => k?.ToString() == "$instanceOf");
        if (hasInstanceOf)
        {
            instances++;
            logger.Info($"  + {key} ($instanceOf : nouveau record)");
            continue;
        }
        // Hache le nom via la fonction officielle de TweakDBID, puis cherche
        // si la TweakDB connaît cet identifiant.
        var hash = WolvenKit.RED4.Types.TweakDBID.CalculateHash(key);
        var resolved = tdb.GetString(hash);
        if (!string.IsNullOrEmpty(resolved))
        {
            ok++;
            logger.Info($"  + {key} (connu)");
        }
        else
        {
            missing++;
            logger.Warning(
                $"  - {key} : inconnu dans TweakDB (ajouter $instanceOf si nouveau record)");
        }
    }
    logger.Info($"tweak validate : {total} cle(s) ; {ok} OK + {instances} instanceOf + {missing} inconnues");
    return missing > 0 ? 1 : 0;
}

/// <summary>Convertit l'arbre brut renvoyé par YamlDotNet (mélange de
/// Dictionary&lt;object,object&gt;, List&lt;object&gt; et scalaires) en arbre
/// 100% JSON-friendly (Dictionary&lt;string,object?&gt; avec clés normalisées
/// et valeurs scalaires typées).</summary>
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
            // YamlDotNet renvoie tout scalaire en string ; on essaie de typer.
            if (long.TryParse(s, out var l)) return l;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dbl)) return dbl;
            if (bool.TryParse(s, out var b)) return b;
            return s;
        default:
            return node?.ToString();
    }
}

/// <summary>Conversion inverse — JsonElement vers l'arbre Dictionary/List
/// que YamlDotNet sait sérialiser.</summary>
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

/// <summary>Résout l'exécutable du jeu à partir d'un chemin : si déjà un .exe,
/// le renvoie ; sinon suppose la racine d'installation et compose
/// bin/x64/Cyberpunk2077.exe (attendu par ArchiveManager.LoadGameArchives).</summary>
static FileInfo ResolveGameExe(string path)
    => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? new FileInfo(path)
        : new FileInfo(Path.Combine(path, "bin", "x64", "Cyberpunk2077.exe"));

// TweakDBService est un singleton gardé chaud (sa TweakDB vit dans un champ
// statique). Sans suivi du chemin, un premier chargement « collait » : interroger
// une AUTRE tweakdb.bin renvoyait les données de la première. On mémorise donc le
// chemin canonique chargé (DaemonState) et on recharge quand il change.
static async Task EnsureTweakDbAsync(ITweakDBService tdb, string path)
{
    var canon = Path.GetFullPath(path);
    if (tdb.IsLoaded && string.Equals(DaemonState.LoadedTweakDbPath, canon, StringComparison.OrdinalIgnoreCase))
        return;
    await tdb.LoadDB(canon);
    if (tdb.IsLoaded) DaemonState.LoadedTweakDbPath = canon;
}

static EUncookExtension? ParseUext(string? s)
    => Enum.TryParse<EUncookExtension>(s, ignoreCase: true, out var e) ? e : null;

static List<uint> ParseUintList(string? s)
{
    var list = new List<uint>();
    if (string.IsNullOrWhiteSpace(s)) return list;
    foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (uint.TryParse(part, out var v)) list.Add(v);
    return list;
}

// ── Protocole IPC ───────────────────────────────────────────────────────
// État partagé du daemon (les fichiers à instructions top-level n'autorisent pas
// de champs statiques au niveau racine — on les regroupe ici).
static class DaemonState
{
    // Chemin canonique de la dernière tweakdb.bin chargée (le service est un
    // singleton chaud ; on recharge quand le chemin change).
    public static string? LoadedTweakDbPath;

    // Flags booléens (sans valeur) — immuable, partagé entre requêtes.
    public static readonly HashSet<string> BoolFlags = new()
    {
        "--list", "--diff", "--wem", "--structured", "--keep",
        "--unbundle", "--serialize", "--mesh-export-lod-filter",
        "--opus-export-all",
    };
}

sealed record DaemonRequest(int id, string[] argv);
sealed record DaemonResponse(int id, int exit, string output);

// ════════════════════════════════════════════════════════════════════════
// Shims — implémentations minimales des interfaces de service de WolvenKit.
// ════════════════════════════════════════════════════════════════════════

/// <summary>ILoggerService qui accumule la sortie dans un tampon drainable.</summary>
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

    private void Append(string level, string s)
    {
        // Même format que le logger de cp77tools : "[ 0: Level ] - message".
        // Le Format() du serveur MCP se fie aux marqueurs ": Success" / ": Error".
        lock (_sb) _sb.AppendLine($"[ 0: {level} ] - {s}");
    }

    /// <summary>Ajoute du texte brut (sortie Console capturée) au même tampon.</summary>
    public void AppendRaw(string s)
    {
        lock (_sb) _sb.Append(s);
    }

    /// <summary>Renvoie la sortie accumulée et vide le tampon.</summary>
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
/// TextWriter qui draine les écritures Console.Out vers le logger capturant.
/// Indispensable : certaines tâches WolvenKit (listing d'archive…) écrivent
/// leur résultat via Console.WriteLine plutôt que par ILoggerService.
/// </summary>
sealed class CapturingTextWriter : TextWriter
{
    private readonly CapturingLoggerService _sink;
    public CapturingTextWriter(CapturingLoggerService sink) => _sink = sink;

    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) => _sink.AppendRaw(value.ToString());
    public override void Write(string? value) { if (value is not null) _sink.AppendRaw(value); }
    public override void WriteLine(string? value) => _sink.AppendRaw((value ?? "") + Environment.NewLine);
}

#pragma warning disable CS0067 // événements jamais déclenchés — service no-op
/// <summary>IProgressService no-op (le daemon ne rend pas de progression).</summary>
sealed class NoopProgressService : IProgressService<double>
{
    public event EventHandler<double>? ProgressChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsIndeterminate { get; set; }
    public EStatus Status { get; set; }

    public void Completed() { }
    public void Report(double value) { }
}
#pragma warning restore CS0067
