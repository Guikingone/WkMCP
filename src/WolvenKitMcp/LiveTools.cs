using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// "Live" MCP tools: drive a <b>running</b> Cyberpunk 2077 game via the
/// Lua mod <b>CETBridge</b> (Cyber Engine Tweaks), as opposed to the offline tools that
/// operate on files. All delegate to <see cref="CetBridge"/> (TCP or file
/// transport). The <c>live_</c> prefix = live game memory; no prefix = files.
///
/// Prerequisites: game launched + Cyber Engine Tweaks + RED4ext (+ RedSocket for TCP; the
/// file fallback works without). See docs/LIVE_BRIDGE.md.
///
/// ⚠ Running Lua in the live game is powerful and risky (can freeze/crash the
/// game). Each execution is protected by <c>pcall</c> on the Lua side and by a timeout on the
/// server side; the TCP listener is restricted to 127.0.0.1.
/// </summary>
[McpServerToolType]
public static class LiveTools
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Wraps a bridge response into stable structured JSON for the agent.</summary>
    internal static string Wrap(string summary, BridgeResponse r) => JsonSerializer.Serialize(new
    {
        ok = r.Ok,
        status = r.Ok ? "success" : "error",
        summary,
        transport = r.Transport,
        result = r.Ok ? r.Result : null,
        error = r.Ok ? null : (r.Error ?? "unknown error"),
        timedOut = r.TimedOut,
    }, JsonOpts);

    private const string GamePathDesc =
        "Root folder of the Cyberpunk 2077 installation. Optional: not needed with TCP transport " +
        "(the mod connects to the server); required for the file fallback to locate the mod " +
        "(<game>/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge), unless CET_BRIDGE_DIR is set.";

    // ── Diagnostic ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "live_status", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Checks the connectivity of the in-game bridge (CETBridge mod). Indicates whether the bridge is " +
                 "connected, through which transport (tcp/file), the listening port, the age of the last " +
                 "heartbeat and the mod folder. Works even with the game off (diagnostic). To call " +
                 "first before any other live_* tool.")]
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
                ? $"Bridge connected via {s.Transport}."
                : "Bridge NOT connected — launch the game with CET + the CETBridge mod (and RedSocket for TCP).",
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

    // ── Lua execution ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "live_execute_lua", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Runs Lua code in the CET console of the live game (loadstring + pcall). For " +
                 "side effects (spawn, state modification…). The output of print() is captured and " +
                 "returned. To READ a value, prefer live_eval. ⚠ Can freeze the game (infinite " +
                 "loop): a server-side timeout protects the agent, not the game.")]
    public static async Task<string> LiveExecuteLua(
        CetBridge bridge,
        [Description("Lua code to run (one or more statements).")] string code,
        [Description(GamePathDesc)] string? gamePath = null,
        CancellationToken ct = default)
    {
        var r = await bridge.ExecAsync(code, gamePath, ct);
        return Wrap("Lua execution (exec)", r);
    }

    [McpServerTool(Name = "live_eval", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Evaluates a Lua EXPRESSION in the live game and returns its serialized value " +
                 "(CET types handled: CName, TweakDBID, Vector4, Quaternion). E.g.: " +
                 "\"Game.GetPlayer():GetLevel()\". To run statements without a return " +
                 "value, use live_execute_lua.")]
    public static async Task<string> LiveEval(
        CetBridge bridge,
        [Description("Lua expression to evaluate (e.g. \"Game.GetPlayer():GetLevel()\").")] string expression,
        [Description(GamePathDesc)] string? gamePath = null,
        CancellationToken ct = default)
    {
        var r = await bridge.EvalAsync(expression, gamePath, ct);
        return Wrap("Lua evaluation (eval)", r);
    }

    [McpServerTool(Name = "live_batch", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Runs several Lua statements in a row in a single round trip (more efficient " +
                 "than N live_execute_lua calls). Each statement is independent: the failure of " +
                 "one does not interrupt the others.")]
    public static async Task<string> LiveBatch(
        CetBridge bridge,
        [Description("List of Lua code snippets run sequentially.")] string[] commands,
        [Description(GamePathDesc)] string? gamePath = null,
        CancellationToken ct = default)
    {
        var r = await bridge.QueryAsync("batch_execute", new { commands }, gamePath, ct);
        return Wrap($"Lua batch ({commands.Length} statements)", r);
    }

    // ── State reading (live game) ───────────────────────────────────────────────

    [McpServerTool(Name = "live_player_info", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("In-game player state: level, street cred, health, position. Requires the player spawned.")]
    public static async Task<string> LivePlayerInfo(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Player info", await bridge.QueryAsync("player_info", null, gamePath, ct));

    [McpServerTool(Name = "live_game_state", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Game state: in-game time, scene tier (gameplay/menu/cinematic), weather, zone type.")]
    public static async Task<string> LiveGameState(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Game state", await bridge.QueryAsync("game_state", null, gamePath, ct));

    [McpServerTool(Name = "live_inventory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists the player's inventory (name, quantity, TweakDBID, quality). Optional filter by type " +
                 "(Weapon, Clothing, Consumable, Gadget, Cyberware, Mod, Crafting, Quest, Junk).")]
    public static async Task<string> LiveInventory(CetBridge bridge,
        [Description("Filter by item type (optional).")] string? type = null,
        [Description("Max number of items (default 50).")] int limit = 50,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Inventory", await bridge.QueryAsync("get_inventory", new { type, limit }, gamePath, ct));

    [McpServerTool(Name = "live_equipped", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Currently equipped items: weapons by slot, clothing, cyberware, quickslots.")]
    public static async Task<string> LiveEquipped(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Equipment", await bridge.QueryAsync("get_equipped", null, gamePath, ct));

    [McpServerTool(Name = "live_active_effects", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Active status effects on the player: ID, remaining duration, number of stacks.")]
    public static async Task<string> LiveActiveEffects(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Active effects", await bridge.QueryAsync("get_active_effects", null, gamePath, ct));

    [McpServerTool(Name = "live_appearance", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Current visual appearance of the player (or a scanned NPC): appearance name + customization.")]
    public static async Task<string> LiveAppearance(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Appearance", await bridge.QueryAsync("get_appearance_info", null, gamePath, ct));

    [McpServerTool(Name = "live_vehicles", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists the vehicles owned by the player (garage): names + TweakDBID.")]
    public static async Task<string> LiveVehicles(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Vehicles", await bridge.QueryAsync("get_vehicle_list", null, gamePath, ct));

    [McpServerTool(Name = "live_nearby_entities", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Scans entities near the player within a radius: name, type, distance, position. " +
                 "Optional filter by type (NPC, Vehicle, Item, Device).")]
    public static async Task<string> LiveNearbyEntities(CetBridge bridge,
        [Description("Search radius in meters (default 20).")] double radius = 20,
        [Description("Filter by entity type (optional).")] string? type = null,
        [Description("Max number of entities (default 20).")] int limit = 20,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Nearby entities", await bridge.QueryAsync("get_nearby_entities", new { radius, type, limit }, gamePath, ct));

    [McpServerTool(Name = "live_scanner", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Detailed info on the entity currently targeted by the player (like a scan): type, name, health, level, faction.")]
    public static async Task<string> LiveScanner(CetBridge bridge,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Scanner", await bridge.QueryAsync("get_scanner_info", null, gamePath, ct));

    // ── Player & world mutation (live game) ──────────────────────────────────────

    [McpServerTool(Name = "live_add_item", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Adds an item to the player's inventory by TweakDBID (e.g. 'Items.Preset_Katana_Saburo').")]
    public static async Task<string> LiveAddItem(CetBridge bridge,
        [Description("Item TweakDBID (e.g. 'Items.Preset_Katana_Saburo').")] string itemId,
        [Description("Quantity (default 1).")] int quantity = 1,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Add item {itemId}", await bridge.QueryAsync("add_item", new { itemId, quantity }, gamePath, ct));

    [McpServerTool(Name = "live_remove_item", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Removes an item from the inventory by TweakDBID. quantity omitted = removes the whole item.")]
    public static async Task<string> LiveRemoveItem(CetBridge bridge,
        [Description("Item TweakDBID.")] string itemId,
        [Description("Quantity to remove (omitted = all).")] int? quantity = null,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Remove item {itemId}", await bridge.QueryAsync("remove_item", new { itemId, quantity }, gamePath, ct));

    [McpServerTool(Name = "live_teleport", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Teleports the player to world coordinates. Use live_player_info for the current position.")]
    public static async Task<string> LiveTeleport(CetBridge bridge,
        [Description("X coordinate.")] double x,
        [Description("Y coordinate.")] double y,
        [Description("Z coordinate.")] double z,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Teleport ({x}, {y}, {z})", await bridge.QueryAsync("teleport", new { x, y, z }, gamePath, ct));

    [McpServerTool(Name = "live_set_stat", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Modifies a player stat. Common stats: Health, Stamina, Armor, Level, StreetCred. " +
                 "Use live_dump_type 'gamedataStatType' to discover the stats.")]
    public static async Task<string> LiveSetStat(CetBridge bridge,
        [Description("Stat name (e.g. 'Health', 'Armor').")] string stat,
        [Description("New value.")] double value,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Stat {stat}={value}", await bridge.QueryAsync("set_stat", new { stat, value }, gamePath, ct));

    [McpServerTool(Name = "live_apply_effect", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Applies a status effect (buff/debuff) to the player. E.g. 'BaseStatusEffect.Berserk'.")]
    public static async Task<string> LiveApplyEffect(CetBridge bridge,
        [Description("Effect TweakDBID (e.g. 'BaseStatusEffect.Berserk').")] string effectId,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Effect applied {effectId}", await bridge.QueryAsync("apply_status_effect", new { effectId }, gamePath, ct));

    [McpServerTool(Name = "live_remove_effect", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Removes a status effect from the player by TweakDBID.")]
    public static async Task<string> LiveRemoveEffect(CetBridge bridge,
        [Description("TweakDBID of the effect to remove.")] string effectId,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Effect removed {effectId}", await bridge.QueryAsync("remove_status_effect", new { effectId }, gamePath, ct));

    [McpServerTool(Name = "live_god_mode", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Enables/disables player invulnerability (useful for testing combat without dying).")]
    public static async Task<string> LiveGodMode(CetBridge bridge,
        [Description("true = enable, false = disable.")] bool enabled,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"God mode {(enabled ? "ON" : "OFF")}", await bridge.QueryAsync("toggle_god_mode", new { enabled }, gamePath, ct));

    [McpServerTool(Name = "live_set_level", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Sets the player's level and/or street cred directly.")]
    public static async Task<string> LiveSetLevel(CetBridge bridge,
        [Description("Player level 1-60 (optional).")] int? level = null,
        [Description("Street cred 1-50 (optional).")] int? streetCred = null,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Level/street cred", await bridge.QueryAsync("set_level", new { level, streetCred }, gamePath, ct));

    [McpServerTool(Name = "live_spawn_vehicle", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Spawns a vehicle near the player (e.g. 'Vehicle.v_sport2_quadra_type66'). " +
                 "Use live_tweakdb_search 'Vehicle.' to find the IDs.")]
    public static async Task<string> LiveSpawnVehicle(CetBridge bridge,
        [Description("Vehicle TweakDBID.")] string vehicleId,
        [Description("Distance in front of the player in meters (default 5).")] double distance = 5,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Spawn vehicle {vehicleId}", await bridge.QueryAsync("spawn_vehicle", new { vehicleId, distance }, gamePath, ct));

    [McpServerTool(Name = "live_set_time", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets the in-game time of day (lighting test, NPC schedules, time-based events).")]
    public static async Task<string> LiveSetTime(CetBridge bridge,
        [Description("Hour 0-23.")] int hours,
        [Description("Minutes 0-59 (default 0).")] int minutes = 0,
        [Description("Seconds 0-59 (default 0).")] int seconds = 0,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Time {hours:00}:{minutes:00}", await bridge.QueryAsync("set_time", new { hours, minutes, seconds }, gamePath, ct));

    [McpServerTool(Name = "live_set_weather", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Changes the in-game weather. Presets: Sunny, Cloudy, Rain, HeavyRain, Fog, Toxic, Sandstorm, Pollution.")]
    public static async Task<string> LiveSetWeather(CetBridge bridge,
        [Description("Weather preset name (e.g. 'Rain', 'Sunny', 'Fog').")] string weather,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Weather {weather}", await bridge.QueryAsync("set_weather", new { weather }, gamePath, ct));

    [McpServerTool(Name = "live_kill_nearby", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Kills hostile NPCs within a radius (encounter testing). allNpcs=true kills ALL NPCs.")]
    public static async Task<string> LiveKillNearby(CetBridge bridge,
        [Description("Radius in meters (default 30).")] double radius = 30,
        [Description("true = all NPCs, false = hostiles only (default).")] bool allNpcs = false,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Kill nearby NPCs", await bridge.QueryAsync("kill_nearby_npcs", new { radius, allNpcs }, gamePath, ct));

    [McpServerTool(Name = "live_notify", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Shows a notification/warning in the game UI (UI testing or reporting).")]
    public static async Task<string> LiveNotify(CetBridge bridge,
        [Description("Message text.")] string message,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap("Notification", await bridge.QueryAsync("show_notification", new { message }, gamePath, ct));

    [McpServerTool(Name = "live_play_sound", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Plays a sound event in-game (e.g. 'ui_menu_hover', 'ui_menu_click', 'w_gun_reload').")]
    public static async Task<string> LivePlaySound(CetBridge bridge,
        [Description("Sound event name.")] string soundEvent,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Sound {soundEvent}", await bridge.QueryAsync("play_sound", new { soundEvent }, gamePath, ct));

    // ── Live-memory TweakDB + RTTI (live game) ───────────────────────────────

    [McpServerTool(Name = "live_tweakdb_get", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads a TweakDB flat or record IN LIVE MEMORY by path (e.g. 'Items.Preset_Katana_Saburo'). " +
                 "Distinct from the offline tools (read_tweak/tweakdb_query): here we read the running game's DB.")]
    public static async Task<string> LiveTweakdbGet(CetBridge bridge,
        [Description("TweakDB record/flat path.")] string path,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"TweakDB get {path}", await bridge.QueryAsync("tweakdb_get", new { path }, gamePath, ct));

    [McpServerTool(Name = "live_tweakdb_set", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Writes a TweakDB flat IN LIVE MEMORY (persists until the game restarts). ⚠ A bad " +
                 "value can crash the game. The type is auto-detected from the value, or forced via `type` " +
                 "(Int, Float, Bool, String, CName).")]
    public static async Task<string> LiveTweakdbSet(CetBridge bridge,
        [Description("Path of the TweakDB flat to modify.")] string path,
        [Description("Value to write (text; converted according to `type` or auto-detected: 'true'/'false'→Bool, integer→Int, decimal→Float, otherwise String).")] string value,
        [Description("Forced type: Int | Float | Bool | String | CName (optional).")] string? type = null,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"TweakDB set {path}", await bridge.QueryAsync("tweakdb_set",
            new { path, value = CoerceTweakValue(value, type), type }, gamePath, ct));

    [McpServerTool(Name = "live_dump_type", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Introspects an RTTI type of the live game (methods, properties, inheritance). E.g. 'PlayerPuppet', " +
                 "'gameItemData'. Distinct from inspect_cr2w (static): here it is the running engine's RTTI.")]
    public static async Task<string> LiveDumpType(CetBridge bridge,
        [Description("Name of the type to introspect (e.g. 'PlayerPuppet').")] string typeName,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"dump_type {typeName}", await bridge.QueryAsync("dump_type", new { typeName }, gamePath, ct));

    [McpServerTool(Name = "live_tweakdb_search", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Searches TweakDB records IN LIVE MEMORY by pattern (substring, case-insensitive). " +
                 "Returns the matching paths. Optional filter by record type.")]
    public static async Task<string> LiveTweakdbSearch(CetBridge bridge,
        [Description("Search pattern (substring).")] string pattern,
        [Description("Filter by record type (e.g. 'gamedataItem_Record', optional).")] string? type = null,
        [Description("Max number of results (default 20).")] int limit = 20,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"TweakDB search '{pattern}'", await bridge.QueryAsync("search_tweakdb", new { pattern, type, limit }, gamePath, ct));

    // ── Quests & events (live game) ──────────────────────────────────────────

    [McpServerTool(Name = "live_get_quest_fact", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads a quest fact (internal progression flag: objectives, dialogue choices, progression).")]
    public static async Task<string> LiveGetQuestFact(CetBridge bridge,
        [Description("Quest fact name (e.g. 'q001_rogue_met').")] string factName,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Quest fact {factName}", await bridge.QueryAsync("get_quest_fact", new { factName }, gamePath, ct));

    [McpServerTool(Name = "live_set_quest_fact", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Sets a quest fact. ⚠ Can break quest progression or unlock content. " +
                 "Value typically 1 (done) or 0 (not done).")]
    public static async Task<string> LiveSetQuestFact(CetBridge bridge,
        [Description("Quest fact name.")] string factName,
        [Description("Value (typically 0 or 1).")] int value,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
        => Wrap($"Set quest fact {factName}={value}", await bridge.QueryAsync("set_quest_fact", new { factName, value }, gamePath, ct));

    // Label → subscription-id registry: lets observations be re-read by
    // "Class/Event" instead of an ephemeral GUID. Lives on the server side (the Lua mod
    // is unchanged); lost if the SERVER restarts — re-run live_observe then.
    private static readonly ConcurrentDictionary<string, string> ObservationLabels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extracts the subscription id from the observe_events handler's response:
    /// JSON object {id|subscriptionId|subscription_id: ...} or a bare id as text.</summary>
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
            // Not JSON: a bare id (without space) is plausible.
            if (!trimmed.Contains(' ') && trimmed.Length <= 64) return trimmed;
        }
        return null;
    }

    [McpServerTool(Name = "live_observe", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Subscribes to a game event via CET's Observe/ObserveAfter. The events are " +
                 "buffered in-game and retrieved via live_observations — either by the returned ID, or by the " +
                 "stable 'Class/Event' label (e.g. 'PlayerPuppet/OnDamageReceived'), memorized on the server side. " +
                 "E.g. class 'PlayerPuppet' / event 'OnDamageReceived'.")]
    public static async Task<string> LiveObserve(CetBridge bridge,
        [Description("Game class name (e.g. 'PlayerPuppet').")] string className,
        [Description("Event/method name (e.g. 'OnDamageReceived').")] string eventName,
        [Description("Max buffer size before overwriting the oldest (default 50).")] int maxBuffer = 50,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
    {
        var resp = await bridge.QueryAsync("observe_events", new { className, eventName, maxBuffer }, gamePath, ct);
        if (resp.Ok && TryExtractSubscriptionId(resp.Result) is { } subId)
            ObservationLabels[$"{className}/{eventName}"] = subId;
        return Wrap($"Observe {className}/{eventName}", resp);
    }

    [McpServerTool(Name = "live_observations", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads (and clears) the observed-event buffer of a subscription created by live_observe. " +
                 "Accepts the raw ID or the 'Class/Event' label (e.g. 'PlayerPuppet/OnDamageReceived').")]
    public static async Task<string> LiveObservations(CetBridge bridge,
        [Description("Subscription ID returned by live_observe, or 'Class/Event' label.")] string subscriptionId,
        [Description(GamePathDesc)] string? gamePath = null, CancellationToken ct = default)
    {
        if (subscriptionId.Contains('/'))
        {
            if (ObservationLabels.TryGetValue(subscriptionId, out var mapped))
                subscriptionId = mapped;
            else
                return Wrap("Observations", new BridgeResponse("", false, null,
                    $"Unknown observation label: {subscriptionId}. The label registry lives in the " +
                    "MCP server (lost on its restart) — re-run live_observe, or pass the raw ID.",
                    false, "n/a"));
        }
        return Wrap("Observations", await bridge.QueryAsync("get_observations", new { subscriptionId }, gamePath, ct));
    }

    /// <summary>Converts a text value + type hint into a JSON value of the right type, so that the Lua
    /// <c>tweakdb_set</c> handler auto-detects correctly (number→Int/Float, bool→Bool, string→String).
    /// CName stays a string (the Lua does CName.new via the `type` hint).</summary>
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
                // Auto-detection (mirror of the Lua handler): bool > int > float > string.
                if (bool.TryParse(value, out var ab)) return ab;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var al)) return al;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ad)) return ad;
                return value;
        }
    }
}
