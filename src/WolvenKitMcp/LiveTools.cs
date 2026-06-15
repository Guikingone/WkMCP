using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// Outils MCP « live » : pilotent un jeu Cyberpunk 2077 <b>en cours d'exécution</b> via le
/// mod Lua <b>CETBridge</b> (Cyber Engine Tweaks), par opposition aux outils offline qui
/// opèrent sur des fichiers. Tous délèguent à <see cref="CetBridge"/> (transport TCP ou
/// fichier). Préfixe <c>live_</c> = mémoire vive du jeu ; sans préfixe = fichiers.
///
/// Prérequis : jeu lancé + Cyber Engine Tweaks + RED4ext (+ RedSocket pour le TCP ; le
/// repli fichier marche sans). Voir docs/LIVE_BRIDGE.md.
///
/// ⚠ Exécuter du Lua dans le jeu vivant est puissant et risqué (peut figer/crasher le
/// jeu). Chaque exécution est protégée par <c>pcall</c> côté Lua et par un timeout côté
/// serveur ; le listener TCP est restreint à 127.0.0.1.
/// </summary>
[McpServerToolType]
public static class LiveTools
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Enveloppe une réponse du pont en JSON structuré stable pour l'agent.</summary>
    internal static string Wrap(string summary, BridgeResponse r) => JsonSerializer.Serialize(new
    {
        ok = r.Ok,
        status = r.Ok ? "success" : "error",
        summary,
        transport = r.Transport,
        result = r.Ok ? r.Result : null,
        error = r.Ok ? null : (r.Error ?? "erreur inconnue"),
        timedOut = r.TimedOut,
    }, JsonOpts);

    private const string GamePathDesc =
        "Dossier racine de l'installation Cyberpunk 2077. Optionnel : inutile en transport TCP " +
        "(le mod se connecte au serveur) ; requis pour le repli fichier afin de localiser le mod " +
        "(<jeu>/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge), sauf si CET_BRIDGE_DIR est défini.";

    // ── Diagnostic ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "live_status", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Vérifie la connectivité du pont in-game (mod CETBridge). Indique si le pont est " +
                 "connecté, par quel transport (tcp/fichier), le port d'écoute, l'âge du dernier " +
                 "heartbeat et le dossier du mod. Fonctionne même jeu éteint (diagnostic). À appeler " +
                 "en premier avant tout autre outil live_*.")]
    public static string LiveStatus(
        CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null)
    {
        var s = bridge.StatusSnapshot(gamePath);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = s.Connected
                ? $"Pont connecté via {s.Transport}."
                : "Pont NON connecté — lance le jeu avec CET + le mod CETBridge (et RedSocket pour le TCP).",
            connected = s.Connected,
            transport = s.Transport,
            tcpListening = s.TcpListening,
            tcpPort = s.TcpPort,
            tcpClientConnected = s.TcpClientConnected,
            fileHeartbeatFresh = s.FileHeartbeatFresh,
            lastHeartbeat = s.LastHeartbeatUtc,
            bridgeDir = s.BridgeDir,
        }, JsonOpts);
    }

    // ── Exécution Lua ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "live_execute_lua", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Exécute du code Lua dans la console CET du jeu vivant (loadstring + pcall). Pour les " +
                 "effets de bord (spawn, modification d'état…). La sortie de print() est capturée et " +
                 "renvoyée. Pour LIRE une valeur, préférer live_eval. ⚠ Peut figer le jeu (boucle " +
                 "infinie) : un timeout côté serveur protège l'agent, pas le jeu.")]
    public static async Task<string> LiveExecuteLua(
        CetBridge bridge,
        [Description("Code Lua à exécuter (une ou plusieurs instructions).")] string code,
        [Description(GamePathDesc)] string? gamePath = null,
        CancellationToken ct = default)
    {
        var r = await bridge.ExecAsync(code, gamePath, ct);
        return Wrap("Exécution Lua (exec)", r);
    }

    [McpServerTool(Name = "live_eval", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Évalue une EXPRESSION Lua dans le jeu vivant et renvoie sa valeur sérialisée " +
                 "(types CET gérés : CName, TweakDBID, Vector4, Quaternion). Ex. : " +
                 "\"Game.GetPlayer():GetLevel()\". Pour exécuter des instructions sans valeur de " +
                 "retour, utiliser live_execute_lua.")]
    public static async Task<string> LiveEval(
        CetBridge bridge,
        [Description("Expression Lua à évaluer (ex. \"Game.GetPlayer():GetLevel()\").")] string expression,
        [Description(GamePathDesc)] string? gamePath = null,
        CancellationToken ct = default)
    {
        var r = await bridge.EvalAsync(expression, gamePath, ct);
        return Wrap("Évaluation Lua (eval)", r);
    }

    [McpServerTool(Name = "live_batch", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Exécute plusieurs instructions Lua à la suite en un seul aller-retour (plus efficace " +
                 "que N appels live_execute_lua). Chaque instruction est indépendante : l'échec de " +
                 "l'une n'interrompt pas les autres.")]
    public static async Task<string> LiveBatch(
        CetBridge bridge,
        [Description("Liste d'extraits de code Lua exécutés séquentiellement.")] string[] commands,
        [Description(GamePathDesc)] string? gamePath = null,
        CancellationToken ct = default)
    {
        var r = await bridge.QueryAsync("batch_execute", new { commands }, gamePath, ct);
        return Wrap($"Batch Lua ({commands.Length} instructions)", r);
    }

    // ── Lecture d'état (jeu vivant) ───────────────────────────────────────────────

    [McpServerTool(Name = "live_player_info", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("État du joueur en jeu : niveau, street cred, santé, position. Nécessite le joueur spawné.")]
    public static async Task<string> LivePlayerInfo(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Infos joueur", await bridge.QueryAsync("player_info", null, gamePath, ct));

    [McpServerTool(Name = "live_game_state", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("État du jeu : heure en jeu, tier de scène (gameplay/menu/cinématique), météo, type de zone.")]
    public static async Task<string> LiveGameState(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("État du jeu", await bridge.QueryAsync("game_state", null, gamePath, ct));

    [McpServerTool(Name = "live_inventory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Liste l'inventaire du joueur (nom, quantité, TweakDBID, qualité). Filtre optionnel par type " +
                 "(Weapon, Clothing, Consumable, Gadget, Cyberware, Mod, Crafting, Quest, Junk).")]
    public static async Task<string> LiveInventory(CetBridge bridge,
        [Description("Filtre par type d'objet (optionnel).")] string? type = null,
        [Description("Nombre max d'objets (défaut 50).")] int limit = 50,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Inventaire", await bridge.QueryAsync("get_inventory", new { type, limit }, gamePath, ct));

    [McpServerTool(Name = "live_equipped", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Objets actuellement équipés : armes par slot, vêtements, cyberware, quickslots.")]
    public static async Task<string> LiveEquipped(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Équipement", await bridge.QueryAsync("get_equipped", null, gamePath, ct));

    [McpServerTool(Name = "live_active_effects", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Effets de statut actifs sur le joueur : ID, durée restante, nombre de stacks.")]
    public static async Task<string> LiveActiveEffects(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Effets actifs", await bridge.QueryAsync("get_active_effects", null, gamePath, ct));

    [McpServerTool(Name = "live_appearance", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Apparence visuelle courante du joueur (ou d'un PNJ scanné) : nom d'apparence + personnalisation.")]
    public static async Task<string> LiveAppearance(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Apparence", await bridge.QueryAsync("get_appearance_info", null, gamePath, ct));

    [McpServerTool(Name = "live_vehicles", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Liste les véhicules possédés par le joueur (garage) : noms + TweakDBID.")]
    public static async Task<string> LiveVehicles(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Véhicules", await bridge.QueryAsync("get_vehicle_list", null, gamePath, ct));

    [McpServerTool(Name = "live_nearby_entities", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Scanne les entités proches du joueur dans un rayon : nom, type, distance, position. " +
                 "Filtre optionnel par type (NPC, Vehicle, Item, Device).")]
    public static async Task<string> LiveNearbyEntities(CetBridge bridge,
        [Description("Rayon de recherche en mètres (défaut 20).")] double radius = 20,
        [Description("Filtre par type d'entité (optionnel).")] string? type = null,
        [Description("Nombre max d'entités (défaut 20).")] int limit = 20,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Entités proches", await bridge.QueryAsync("get_nearby_entities", new { radius, type, limit }, gamePath, ct));

    [McpServerTool(Name = "live_scanner", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Infos détaillées sur l'entité actuellement visée par le joueur (comme un scan) : type, nom, santé, niveau, faction.")]
    public static async Task<string> LiveScanner(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Scanner", await bridge.QueryAsync("get_scanner_info", null, gamePath, ct));

    // ── Mutation joueur & monde (jeu vivant) ──────────────────────────────────────

    [McpServerTool(Name = "live_add_item", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Ajoute un objet à l'inventaire du joueur par TweakDBID (ex. 'Items.Preset_Katana_Saburo').")]
    public static async Task<string> LiveAddItem(CetBridge bridge,
        [Description("TweakDBID de l'objet (ex. 'Items.Preset_Katana_Saburo').")] string itemId,
        [Description("Quantité (défaut 1).")] int quantity = 1,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Ajout objet {itemId}", await bridge.QueryAsync("add_item", new { itemId, quantity }, gamePath, ct));

    [McpServerTool(Name = "live_remove_item", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Retire un objet de l'inventaire par TweakDBID. quantity omis = retire tout l'objet.")]
    public static async Task<string> LiveRemoveItem(CetBridge bridge,
        [Description("TweakDBID de l'objet.")] string itemId,
        [Description("Quantité à retirer (omis = tout).")] int? quantity = null,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Retrait objet {itemId}", await bridge.QueryAsync("remove_item", new { itemId, quantity }, gamePath, ct));

    [McpServerTool(Name = "live_teleport", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Téléporte le joueur à des coordonnées monde. Utiliser live_player_info pour la position courante.")]
    public static async Task<string> LiveTeleport(CetBridge bridge,
        [Description("Coordonnée X.")] double x,
        [Description("Coordonnée Y.")] double y,
        [Description("Coordonnée Z.")] double z,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Téléportation ({x}, {y}, {z})", await bridge.QueryAsync("teleport", new { x, y, z }, gamePath, ct));

    [McpServerTool(Name = "live_set_stat", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Modifie une stat du joueur. Stats courantes : Health, Stamina, Armor, Level, StreetCred. " +
                 "Utiliser live_dump_type 'gamedataStatType' pour découvrir les stats.")]
    public static async Task<string> LiveSetStat(CetBridge bridge,
        [Description("Nom de la stat (ex. 'Health', 'Armor').")] string stat,
        [Description("Nouvelle valeur.")] double value,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Stat {stat}={value}", await bridge.QueryAsync("set_stat", new { stat, value }, gamePath, ct));

    [McpServerTool(Name = "live_apply_effect", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Applique un effet de statut (buff/debuff) au joueur. Ex. 'BaseStatusEffect.Berserk'.")]
    public static async Task<string> LiveApplyEffect(CetBridge bridge,
        [Description("TweakDBID de l'effet (ex. 'BaseStatusEffect.Berserk').")] string effectId,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Effet appliqué {effectId}", await bridge.QueryAsync("apply_status_effect", new { effectId }, gamePath, ct));

    [McpServerTool(Name = "live_remove_effect", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Retire un effet de statut du joueur par TweakDBID.")]
    public static async Task<string> LiveRemoveEffect(CetBridge bridge,
        [Description("TweakDBID de l'effet à retirer.")] string effectId,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Effet retiré {effectId}", await bridge.QueryAsync("remove_status_effect", new { effectId }, gamePath, ct));

    [McpServerTool(Name = "live_god_mode", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Active/désactive l'invulnérabilité du joueur (utile pour tester du combat sans mourir).")]
    public static async Task<string> LiveGodMode(CetBridge bridge,
        [Description("true = active, false = désactive.")] bool enabled,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"God mode {(enabled ? "ON" : "OFF")}", await bridge.QueryAsync("toggle_god_mode", new { enabled }, gamePath, ct));

    [McpServerTool(Name = "live_set_level", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Définit le niveau et/ou le street cred du joueur directement.")]
    public static async Task<string> LiveSetLevel(CetBridge bridge,
        [Description("Niveau joueur 1-60 (optionnel).")] int? level = null,
        [Description("Street cred 1-50 (optionnel).")] int? streetCred = null,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Niveau/street cred", await bridge.QueryAsync("set_level", new { level, streetCred }, gamePath, ct));

    [McpServerTool(Name = "live_spawn_vehicle", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Fait apparaître un véhicule près du joueur (ex. 'Vehicle.v_sport2_quadra_type66'). " +
                 "Utiliser live_tweakdb_search 'Vehicle.' pour trouver les IDs.")]
    public static async Task<string> LiveSpawnVehicle(CetBridge bridge,
        [Description("TweakDBID du véhicule.")] string vehicleId,
        [Description("Distance devant le joueur en mètres (défaut 5).")] double distance = 5,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Spawn véhicule {vehicleId}", await bridge.QueryAsync("spawn_vehicle", new { vehicleId, distance }, gamePath, ct));

    [McpServerTool(Name = "live_set_time", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Règle l'heure du jour en jeu (test d'éclairage, horaires PNJ, événements temporels).")]
    public static async Task<string> LiveSetTime(CetBridge bridge,
        [Description("Heure 0-23.")] int hours,
        [Description("Minutes 0-59 (défaut 0).")] int minutes = 0,
        [Description("Secondes 0-59 (défaut 0).")] int seconds = 0,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Heure {hours:00}:{minutes:00}", await bridge.QueryAsync("set_time", new { hours, minutes, seconds }, gamePath, ct));

    [McpServerTool(Name = "live_set_weather", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Change la météo en jeu. Presets : Sunny, Cloudy, Rain, HeavyRain, Fog, Toxic, Sandstorm, Pollution.")]
    public static async Task<string> LiveSetWeather(CetBridge bridge,
        [Description("Nom du preset météo (ex. 'Rain', 'Sunny', 'Fog').")] string weather,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Météo {weather}", await bridge.QueryAsync("set_weather", new { weather }, gamePath, ct));

    [McpServerTool(Name = "live_kill_nearby", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Tue les PNJ hostiles dans un rayon (test d'encounters). allNpcs=true tue TOUS les PNJ.")]
    public static async Task<string> LiveKillNearby(CetBridge bridge,
        [Description("Rayon en mètres (défaut 30).")] double radius = 30,
        [Description("true = tous les PNJ, false = hostiles seulement (défaut).")] bool allNpcs = false,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Kill PNJ proches", await bridge.QueryAsync("kill_nearby_npcs", new { radius, allNpcs }, gamePath, ct));

    [McpServerTool(Name = "live_notify", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Affiche une notification/avertissement dans l'UI du jeu (test d'UI ou signalement).")]
    public static async Task<string> LiveNotify(CetBridge bridge,
        [Description("Texte du message.")] string message,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Notification", await bridge.QueryAsync("show_notification", new { message }, gamePath, ct));

    [McpServerTool(Name = "live_play_sound", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Joue un événement sonore en jeu (ex. 'ui_menu_hover', 'ui_menu_click', 'w_gun_reload').")]
    public static async Task<string> LivePlaySound(CetBridge bridge,
        [Description("Nom de l'événement sonore.")] string soundEvent,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Son {soundEvent}", await bridge.QueryAsync("play_sound", new { soundEvent }, gamePath, ct));

    // ── TweakDB en mémoire vive + RTTI (jeu vivant) ───────────────────────────────

    [McpServerTool(Name = "live_tweakdb_get", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lit un flat ou record TweakDB EN MÉMOIRE VIVE par chemin (ex. 'Items.Preset_Katana_Saburo'). " +
                 "Distinct des outils offline (read_tweak/tweakdb_query) : ici on lit la DB du jeu en cours.")]
    public static async Task<string> LiveTweakdbGet(CetBridge bridge,
        [Description("Chemin du record/flat TweakDB.")] string path,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"TweakDB get {path}", await bridge.QueryAsync("tweakdb_get", new { path }, gamePath, ct));

    [McpServerTool(Name = "live_tweakdb_set", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Écrit un flat TweakDB EN MÉMOIRE VIVE (persiste jusqu'au redémarrage du jeu). ⚠ Une mauvaise " +
                 "valeur peut crasher le jeu. Le type est auto-détecté depuis la valeur, ou forcé via `type` " +
                 "(Int, Float, Bool, String, CName).")]
    public static async Task<string> LiveTweakdbSet(CetBridge bridge,
        [Description("Chemin du flat TweakDB à modifier.")] string path,
        [Description("Valeur à écrire (texte ; convertie selon `type` ou auto-détectée : 'true'/'false'→Bool, entier→Int, décimal→Float, sinon String).")] string value,
        [Description("Type forcé : Int | Float | Bool | String | CName (optionnel).")] string? type = null,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"TweakDB set {path}", await bridge.QueryAsync("tweakdb_set",
            new { path, value = CoerceTweakValue(value, type), type }, gamePath, ct));

    [McpServerTool(Name = "live_dump_type", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Introspecte un type RTTI du jeu vivant (méthodes, propriétés, héritage). Ex. 'PlayerPuppet', " +
                 "'gameItemData'. Distinct de inspect_cr2w (statique) : ici c'est le RTTI du moteur en cours.")]
    public static async Task<string> LiveDumpType(CetBridge bridge,
        [Description("Nom du type à introspecter (ex. 'PlayerPuppet').")] string typeName,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"dump_type {typeName}", await bridge.QueryAsync("dump_type", new { typeName }, gamePath, ct));

    [McpServerTool(Name = "live_tweakdb_search", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Cherche des records TweakDB EN MÉMOIRE VIVE par motif (sous-chaîne, insensible à la casse). " +
                 "Renvoie les chemins correspondants. Filtre optionnel par type de record.")]
    public static async Task<string> LiveTweakdbSearch(CetBridge bridge,
        [Description("Motif de recherche (sous-chaîne).")] string pattern,
        [Description("Filtre par type de record (ex. 'gamedataItem_Record', optionnel).")] string? type = null,
        [Description("Nombre max de résultats (défaut 20).")] int limit = 20,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"TweakDB search '{pattern}'", await bridge.QueryAsync("search_tweakdb", new { pattern, type, limit }, gamePath, ct));

    // ── Quêtes & événements (jeu vivant) ──────────────────────────────────────────

    [McpServerTool(Name = "live_get_quest_fact", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lit un quest fact (drapeau de progression interne : objectifs, choix de dialogue, progression).")]
    public static async Task<string> LiveGetQuestFact(CetBridge bridge,
        [Description("Nom du quest fact (ex. 'q001_rogue_met').")] string factName,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Quest fact {factName}", await bridge.QueryAsync("get_quest_fact", new { factName }, gamePath, ct));

    [McpServerTool(Name = "live_set_quest_fact", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Définit un quest fact. ⚠ Peut casser la progression de quête ou débloquer du contenu. " +
                 "Valeur typiquement 1 (fait) ou 0 (pas fait).")]
    public static async Task<string> LiveSetQuestFact(CetBridge bridge,
        [Description("Nom du quest fact.")] string factName,
        [Description("Valeur (typiquement 0 ou 1).")] int value,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Set quest fact {factName}={value}", await bridge.QueryAsync("set_quest_fact", new { factName, value }, gamePath, ct));

    // Registre label → id d'abonnement : permet de relire les observations par
    // « Classe/Event » au lieu d'un GUID éphémère. Vit côté serveur (le mod Lua
    // est inchangé) ; perdu si le SERVEUR redémarre — refaire live_observe alors.
    private static readonly ConcurrentDictionary<string, string> ObservationLabels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extrait l'id d'abonnement de la réponse du handler observe_events :
    /// objet JSON {id|subscriptionId|subscription_id: ...} ou id nu en texte.</summary>
    internal static string? TryExtractSubscriptionId(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        var trimmed = result.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "id", "subscriptionId", "subscription_id" })
                    if (root.TryGetProperty(key, out var el))
                        return el.ValueKind switch
                        {
                            JsonValueKind.String => el.GetString(),
                            JsonValueKind.Number => el.GetRawText(),
                            _ => null,
                        };
                return null;
            }
            if (root.ValueKind == JsonValueKind.String) return root.GetString();
            if (root.ValueKind == JsonValueKind.Number) return root.GetRawText();
        }
        catch (JsonException)
        {
            // Pas du JSON : un id nu (sans espace) est plausible.
            if (!trimmed.Contains(' ') && trimmed.Length <= 64) return trimmed;
        }
        return null;
    }

    [McpServerTool(Name = "live_observe", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("S'abonne à un événement de jeu via Observe/ObserveAfter de CET. Les événements sont mis en " +
                 "tampon en jeu et récupérés via live_observations — soit par l'ID renvoyé, soit par le label " +
                 "stable 'Classe/Event' (ex. 'PlayerPuppet/OnDamageReceived'), mémorisé côté serveur. " +
                 "Ex. classe 'PlayerPuppet' / event 'OnDamageReceived'.")]
    public static async Task<string> LiveObserve(CetBridge bridge,
        [Description("Nom de classe du jeu (ex. 'PlayerPuppet').")] string className,
        [Description("Nom de l'événement/méthode (ex. 'OnDamageReceived').")] string eventName,
        [Description("Taille max du tampon avant d'écraser les plus anciens (défaut 50).")] int maxBuffer = 50,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
    {
        var resp = await bridge.QueryAsync("observe_events", new { className, eventName, maxBuffer }, gamePath, ct);
        if (resp.Ok && TryExtractSubscriptionId(resp.Result) is { } subId)
            ObservationLabels[$"{className}/{eventName}"] = subId;
        return Wrap($"Observe {className}/{eventName}", resp);
    }

    [McpServerTool(Name = "live_observations", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lit (et vide) le tampon d'événements observés d'un abonnement créé par live_observe. " +
                 "Accepte l'ID brut ou le label 'Classe/Event' (ex. 'PlayerPuppet/OnDamageReceived').")]
    public static async Task<string> LiveObservations(CetBridge bridge,
        [Description("ID d'abonnement renvoyé par live_observe, ou label 'Classe/Event'.")] string subscriptionId,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
    {
        if (subscriptionId.Contains('/'))
        {
            if (ObservationLabels.TryGetValue(subscriptionId, out var mapped))
                subscriptionId = mapped;
            else
                return Wrap("Observations", new BridgeResponse("", false, null,
                    $"Label d'observation inconnu : {subscriptionId}. Le registre des labels vit dans le " +
                    "serveur MCP (perdu à son redémarrage) — relancer live_observe, ou passer l'ID brut.",
                    false, "n/a"));
        }
        return Wrap("Observations", await bridge.QueryAsync("get_observations", new { subscriptionId }, gamePath, ct));
    }

    /// <summary>Convertit une valeur texte + hint de type en valeur JSON du bon type, pour que le handler
    /// Lua <c>tweakdb_set</c> auto-détecte correctement (number→Int/Float, bool→Bool, string→String).
    /// CName reste une string (le Lua fait CName.new via le hint `type`).</summary>
    internal static object CoerceTweakValue(string value, string? type)
    {
        switch ((type ?? "").Trim().ToLowerInvariant())
        {
            case "int":
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : value;
            case "float":
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : value;
            case "bool":
                return bool.TryParse(value, out var b) ? b : value;
            case "string":
            case "cname":
                return value;
            default:
                // Auto-détection (miroir du handler Lua) : bool > int > float > string.
                if (bool.TryParse(value, out var ab)) return ab;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var al)) return al;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ad)) return ad;
                return value;
        }
    }
}
