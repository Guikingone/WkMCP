local serializer = require("serializer")

local handlers = {}

function handlers.player_info()
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available (not in game or loading)"
    end

    local statsSystem = Game.GetStatsSystem()
    local playerID = player:GetEntityID()

    local level = statsSystem:GetStatValue(playerID, gamedataStatType.Level)
    local streetCred = statsSystem:GetStatValue(playerID, gamedataStatType.StreetCred)

    local health = 0
    local maxHealth = 0
    local ok, err = pcall(function()
        local statPoolSystem = Game.GetStatPoolsSystem()
        health = statPoolSystem:GetStatPoolValue(playerID, gamedataStatPoolType.Health)
        maxHealth = statsSystem:GetStatValue(playerID, gamedataStatType.Health)
    end)

    local pos = player:GetWorldPosition()
    local posData = nil
    if pos then
        local pok, _ = pcall(function()
            posData = {x = pos.x, y = pos.y, z = pos.z}
        end)
    end

    local info = {
        level = math.floor(level),
        streetCred = math.floor(streetCred),
        health = math.floor(health),
        maxHealth = math.floor(maxHealth),
        position = posData
    }

    return info
end

function handlers.game_state()
    local timeSystem = Game.GetTimeSystem()
    local gameTime = timeSystem:GetGameTime()

    local hours = GameTime.Hours(gameTime)
    local minutes = GameTime.Minutes(gameTime)

    local sceneTier = "unknown"
    local ok1, _ = pcall(function()
        local player = Game.GetPlayer()
        if player then
            local blackboard = Game.GetBlackboardSystem():GetLocalInstanced(player:GetEntityID(), GetAllBlackboardDefs().PlayerStateMachine)
            if blackboard then
                local tier = blackboard:GetInt(GetAllBlackboardDefs().PlayerStateMachine.SceneTier)
                local tierMap = {
                    [0] = "Tier1_FullGameplay",
                    [1] = "Tier2_StagedGameplay",
                    [2] = "Tier3_LimitedGameplay",
                    [3] = "Tier4_FPPCinematic",
                    [4] = "Tier5_Cinematic"
                }
                sceneTier = tierMap[tier] or ("Tier" .. tostring(tier))
            end
        end
    end)

    local weather = "unknown"
    local ok2, _ = pcall(function()
        local weatherSystem = Game.GetWeatherSystem()
        if weatherSystem then
            weather = NameToString(weatherSystem:GetWeatherState())
        end
    end)

    local zoneType = "unknown"
    local ok3, _ = pcall(function()
        local player = Game.GetPlayer()
        if player then
            local preventionSystem = Game.GetPreventionSystem()
            if preventionSystem then
                local zone = preventionSystem:GetCurrentSecurityZoneType(player)
                zoneType = tostring(zone)
            end
        end
    end)

    local state = {
        time = string.format("%02d:%02d", hours, minutes),
        sceneTier = sceneTier,
        weather = weather,
        zoneType = zoneType
    }

    return state
end

function handlers.tweakdb_get(args)
    if not args or not args.path then
        return nil, "No TweakDB path provided"
    end

    local path = args.path
    local tdbid = TweakDBID.new(path)

    -- Try as flat first
    local ok, flat = pcall(TweakDB.GetFlat, TweakDB, tdbid)
    if ok and flat ~= nil then
        return {path = path, type = "flat", value = flat}
    end

    -- Try as record
    local rok, record = pcall(TweakDB.GetRecord, TweakDB, tdbid)
    if rok and record then
        local info = {path = path, type = "record", className = "unknown", flats = {}}

        pcall(function()
            info.className = record:GetClassName().value
        end)

        -- Get flats under this record
        local fok, flatList = pcall(TweakDB.GetRecordFlats, TweakDB, tdbid)
        if fok and flatList then
            for _, flatId in ipairs(flatList) do
                local fname = TDBID.ToStringDEBUG(flatId)
                local fval = TweakDB:GetFlat(flatId)
                table.insert(info.flats, {name = fname, value = fval})
            end
        end

        return info
    end

    return nil, "TweakDB path not found: " .. path
end

function handlers.tweakdb_set(args)
    if not args or not args.path then
        return nil, "No TweakDB path provided"
    end
    if args.value == nil then
        return nil, "No value provided"
    end

    local path = args.path
    local value = args.value
    local valType = args.type

    -- Auto-detect type if not specified
    if not valType then
        local luaType = type(value)
        if luaType == "number" then
            valType = (math.floor(value) == value) and "Int" or "Float"
        elseif luaType == "boolean" then
            valType = "Bool"
        elseif luaType == "string" then
            valType = "String"
        end
    end

    -- Convert value to appropriate type
    if valType == "CName" then
        value = CName.new(value)
    end

    local ok, err = pcall(function()
        TweakDB:SetFlat(TweakDBID.new(path), value)
    end)

    if not ok then
        return nil, "Failed to set TweakDB value: " .. tostring(err)
    end

    return {path = path, value = value, type = valType, success = true}
end

function handlers.dump_type(args)
    if not args or not args.typeName then
        return nil, "No type name provided"
    end

    local typeName = args.typeName
    local rtti = Game.GetRTTISystem()
    local typeObj = rtti:GetClass(CName.new(typeName))

    if not typeObj then
        return nil, "Type not found: " .. typeName
    end

    local info = {
        name = typeName,
        parent = nil,
        properties = {},
        methods = {}
    }

    pcall(function()
        local parent = typeObj:GetParent()
        if parent then
            info.parent = NameToString(parent:GetName())
        end
    end)

    pcall(function()
        local props = typeObj:GetProperties()
        for _, prop in ipairs(props) do
            table.insert(info.properties, {
                name = NameToString(prop:GetName()),
                type = NameToString(prop:GetType():GetName())
            })
        end
    end)

    pcall(function()
        local funcs = typeObj:GetFunctions()
        for _, func in ipairs(funcs) do
            local funcInfo = {
                name = NameToString(func:GetName()),
                returnType = nil,
                params = {}
            }

            pcall(function()
                local ret = func:GetReturnType()
                if ret then
                    funcInfo.returnType = NameToString(ret:GetType():GetName())
                end
            end)

            pcall(function()
                local params = func:GetParameters()
                for _, param in ipairs(params) do
                    table.insert(funcInfo.params, {
                        name = NameToString(param:GetName()),
                        type = NameToString(param:GetType():GetName())
                    })
                end
            end)

            table.insert(info.methods, funcInfo)
        end
    end)

    return info
end

-- Phase 3 handlers

local subscriptions = {}
local subCounter = 0

function handlers.observe_events(args)
    if not args or not args.className or not args.eventName then
        return nil, "className and eventName are required"
    end

    subCounter = subCounter + 1
    local subId = "sub_" .. tostring(subCounter)
    local maxBuffer = args.maxBuffer or 50

    subscriptions[subId] = {
        className = args.className,
        eventName = args.eventName,
        events = {},
        maxBuffer = maxBuffer
    }

    local ok, err = pcall(function()
        ObserveAfter(args.className, args.eventName, function(self, ...)
            local sub = subscriptions[subId]
            if not sub then return end

            local entry = {
                timestamp = os.date("!%Y-%m-%dT%H:%M:%SZ"),
                args = {}
            }

            local eventArgs = {...}
            for i, arg in ipairs(eventArgs) do
                local aok, aval = pcall(tostring, arg)
                entry.args[i] = aok and aval or "[unserializable]"
            end

            table.insert(sub.events, entry)
            if #sub.events > sub.maxBuffer then
                table.remove(sub.events, 1)
            end
        end)
    end)

    if not ok then
        subscriptions[subId] = nil
        return nil, "Failed to observe: " .. tostring(err)
    end

    return {
        subscriptionId = subId,
        className = args.className,
        eventName = args.eventName,
        maxBuffer = maxBuffer
    }
end

function handlers.get_observations(args)
    if not args or not args.subscriptionId then
        return nil, "subscriptionId is required"
    end

    local sub = subscriptions[args.subscriptionId]
    if not sub then
        return nil, "Subscription not found: " .. tostring(args.subscriptionId)
    end

    local events = sub.events
    sub.events = {}

    return {
        subscriptionId = args.subscriptionId,
        className = sub.className,
        eventName = sub.eventName,
        count = #events,
        events = events
    }
end

function handlers.batch_execute(args)
    if not args or not args.commands then
        return nil, "commands array is required"
    end

    local results = {}
    for i, code in ipairs(args.commands) do
        local fn, loadErr = loadstring(code)
        if not fn then
            results[i] = {ok = false, error = "Syntax error: " .. tostring(loadErr)}
        else
            local ok, execErr = pcall(fn)
            if ok then
                results[i] = {ok = true}
            else
                results[i] = {ok = false, error = "Runtime error: " .. tostring(execErr)}
            end
        end
    end

    return {count = #results, results = results}
end

function handlers.add_item(args)
    if not args or not args.itemId then
        return nil, "itemId is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local quantity = args.quantity or 1
    local tdbid = TweakDBID.new(args.itemId)

    local ok, err = pcall(function()
        local transSystem = Game.GetTransactionSystem()
        local itemID = ItemID.new(tdbid)
        transSystem:GiveItem(player, itemID, quantity)
    end)

    if not ok then
        return nil, "Failed to add item: " .. tostring(err)
    end

    return {itemId = args.itemId, quantity = quantity, success = true}
end

function handlers.teleport(args)
    if not args or not args.x or not args.y or not args.z then
        return nil, "x, y, z coordinates are required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        local pos = Vector4.new(args.x, args.y, args.z, 1.0)
        local angle = player:GetWorldOrientation():IsValid() and player:GetWorldOrientation() or EulerAngles.new(0, 0, 0):ToQuat()
        Game.GetTeleportationFacility():Teleport(player, pos, angle)
    end)

    if not ok then
        return nil, "Failed to teleport: " .. tostring(err)
    end

    return {x = args.x, y = args.y, z = args.z, success = true}
end

function handlers.search_tweakdb(args)
    if not args or not args.pattern then
        return nil, "pattern is required"
    end

    local pattern = string.lower(args.pattern)
    local limit = args.limit or 20
    local filterType = args.type

    local results = {}
    local count = 0

    local ok, err = pcall(function()
        local flatList = TweakDB:GetRecords()
        if not flatList then return end

        for _, record in ipairs(flatList) do
            if count >= limit then break end

            local rok, path = pcall(function()
                return TDBID.ToStringDEBUG(record:GetID())
            end)

            if rok and path and string.find(string.lower(path), pattern, 1, true) then
                local entry = {path = path}

                if filterType then
                    local tok, typeName = pcall(function()
                        return NameToString(record:GetClassName())
                    end)
                    if tok and typeName == filterType then
                        entry.type = typeName
                        table.insert(results, entry)
                        count = count + 1
                    end
                else
                    pcall(function()
                        entry.type = NameToString(record:GetClassName())
                    end)
                    table.insert(results, entry)
                    count = count + 1
                end
            end
        end
    end)

    if not ok then
        return nil, "Search failed: " .. tostring(err)
    end

    return {pattern = args.pattern, count = count, results = results}
end

-- Inventory handlers

function handlers.get_inventory(args)
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local limit = (args and args.limit) or 50
    local filterType = args and args.type
    local items = {}
    local count = 0

    local ok, err = pcall(function()
        local transSystem = Game.GetTransactionSystem()
        local itemList = transSystem:GetItemList(player)

        for _, itemData in ipairs(itemList) do
            if count >= limit then break end

            local itemId = itemData:GetID()
            local tdbId = itemId.tdbid
            local path = TDBID.ToStringDEBUG(tdbId)

            local itemType = "Unknown"
            pcall(function()
                local record = TweakDB:GetRecord(tdbId)
                if record then
                    itemType = NameToString(record:GetClassName())
                end
            end)

            local shouldInclude = true
            if filterType then
                shouldInclude = string.find(itemType, filterType, 1, true) ~= nil
            end

            if shouldInclude then
                local entry = {
                    id = path,
                    type = itemType,
                    quantity = transSystem:GetItemQuantity(player, itemId)
                }

                pcall(function()
                    local nameRecord = TweakDB:GetRecord(tdbId)
                    if nameRecord then
                        local locKey = nameRecord:DisplayName()
                        if locKey then
                            entry.name = Game.GetLocalizedText(locKey)
                        end
                    end
                end)

                table.insert(items, entry)
                count = count + 1
            end
        end
    end)

    if not ok then
        return nil, "Failed to get inventory: " .. tostring(err)
    end

    return {count = count, items = items}
end

function handlers.remove_item(args)
    if not args or not args.itemId then
        return nil, "itemId is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        local transSystem = Game.GetTransactionSystem()
        local tdbid = TweakDBID.new(args.itemId)
        local itemID = ItemID.new(tdbid)

        if args.quantity then
            transSystem:RemoveItem(player, itemID, args.quantity)
        else
            local qty = transSystem:GetItemQuantity(player, itemID)
            if qty > 0 then
                transSystem:RemoveItem(player, itemID, qty)
            end
        end
    end)

    if not ok then
        return nil, "Failed to remove item: " .. tostring(err)
    end

    return {itemId = args.itemId, quantity = args.quantity or "all", success = true}
end

function handlers.get_equipped(args)
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local equipped = {weapons = {}, clothing = {}}

    local ok, err = pcall(function()
        local transSystem = Game.GetTransactionSystem()
        local equipSystem = Game.GetScriptableSystemsContainer():Get("EquipmentSystem")

        -- Get equipped weapons (3 weapon slots)
        for slot = 0, 2 do
            pcall(function()
                local slotName = "AttachmentSlots.WeaponRight"
                if slot == 1 then slotName = "AttachmentSlots.WeaponLeft" end
                if slot == 2 then slotName = "AttachmentSlots.WeaponHeavy" end

                local itemObj = transSystem:GetItemInSlot(player, TweakDBID.new(slotName))
                if itemObj then
                    local itemId = itemObj:GetItemID()
                    local path = TDBID.ToStringDEBUG(itemId.tdbid)
                    table.insert(equipped.weapons, {
                        slot = slot,
                        id = path
                    })
                end
            end)
        end

        -- Get equipped clothing
        local clothingSlots = {
            "AttachmentSlots.Outfit",
            "AttachmentSlots.Head",
            "AttachmentSlots.Face",
            "AttachmentSlots.InnerChest",
            "AttachmentSlots.OuterChest",
            "AttachmentSlots.Legs",
            "AttachmentSlots.Feet"
        }

        for _, slotName in ipairs(clothingSlots) do
            pcall(function()
                local itemObj = transSystem:GetItemInSlot(player, TweakDBID.new(slotName))
                if itemObj then
                    local itemId = itemObj:GetItemID()
                    local path = TDBID.ToStringDEBUG(itemId.tdbid)
                    table.insert(equipped.clothing, {
                        slot = slotName,
                        id = path
                    })
                end
            end)
        end
    end)

    if not ok then
        return nil, "Failed to get equipment: " .. tostring(err)
    end

    return equipped
end

-- Player stat handlers

function handlers.set_stat(args)
    if not args or not args.stat or not args.value then
        return nil, "stat and value are required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        local statType = gamedataStatType[args.stat]
        if not statType then
            error("Unknown stat type: " .. tostring(args.stat))
        end

        local statsSystem = Game.GetStatsSystem()
        local playerID = player:GetEntityID()
        local modGroup = statsSystem:GetModifierGroupForObject(playerID)

        -- Remove existing modifiers and add a flat modifier
        local modifier = gameStatModifierData.new()
        modifier.modifierType = gameStatModifierType.Additive
        modifier.statType = statType
        modifier.value = args.value

        statsSystem:AddModifier(playerID, modifier)
    end)

    if not ok then
        return nil, "Failed to set stat: " .. tostring(err)
    end

    return {stat = args.stat, value = args.value, success = true}
end

function handlers.apply_status_effect(args)
    if not args or not args.effectId then
        return nil, "effectId is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        local ses = Game.GetStatusEffectSystem()
        local tdbid = TweakDBID.new(args.effectId)
        ses:ApplyStatusEffect(player:GetEntityID(), tdbid)
    end)

    if not ok then
        return nil, "Failed to apply status effect: " .. tostring(err)
    end

    return {effectId = args.effectId, applied = true}
end

function handlers.remove_status_effect(args)
    if not args or not args.effectId then
        return nil, "effectId is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        local ses = Game.GetStatusEffectSystem()
        local tdbid = TweakDBID.new(args.effectId)
        ses:RemoveStatusEffect(player:GetEntityID(), tdbid)
    end)

    if not ok then
        return nil, "Failed to remove status effect: " .. tostring(err)
    end

    return {effectId = args.effectId, removed = true}
end

-- World handlers

function handlers.spawn_vehicle(args)
    if not args or not args.vehicleId then
        return nil, "vehicleId is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        local distance = args.distance or 5
        local pos = player:GetWorldPosition()
        local forward = player:GetWorldForward()

        local spawnPos = Vector4.new(
            pos.x + forward.x * distance,
            pos.y + forward.y * distance,
            pos.z,
            1.0
        )

        Game.GetVehicleSystem():SpawnPlayerVehicle(
            TweakDBID.new(args.vehicleId)
        )
    end)

    if not ok then
        return nil, "Failed to spawn vehicle: " .. tostring(err)
    end

    return {vehicleId = args.vehicleId, success = true}
end

function handlers.get_nearby_entities(args)
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local radius = (args and args.radius) or 20
    local filterType = args and args.type
    local limit = (args and args.limit) or 20
    local entities = {}
    local count = 0

    local ok, err = pcall(function()
        local searchQuery = Game['TSQ_ALL;']()
        searchQuery.maxDistance = radius

        local playerPos = player:GetWorldPosition()
        local found = Game.GetTargetingSystem():GetTargetParts(player, searchQuery)

        if not found then return end

        for _, targetPart in ipairs(found) do
            if count >= limit then break end

            pcall(function()
                local entity = targetPart:GetComponent():GetEntity()
                if not entity then return end

                local entityType = "Unknown"
                local className = NameToString(entity:GetClassName())

                if string.find(className, "NPC") or string.find(className, "Puppet") then
                    entityType = "NPC"
                elseif string.find(className, "Vehicle") or string.find(className, "vehicle") then
                    entityType = "Vehicle"
                elseif string.find(className, "Item") or string.find(className, "item") then
                    entityType = "Item"
                elseif string.find(className, "Device") or string.find(className, "device") then
                    entityType = "Device"
                end

                local shouldInclude = true
                if filterType then
                    shouldInclude = (entityType == filterType)
                end

                if shouldInclude then
                    local entPos = entity:GetWorldPosition()
                    local dx = entPos.x - playerPos.x
                    local dy = entPos.y - playerPos.y
                    local dz = entPos.z - playerPos.z
                    local dist = math.sqrt(dx*dx + dy*dy + dz*dz)

                    local entry = {
                        className = className,
                        type = entityType,
                        distance = math.floor(dist * 10) / 10,
                        position = {x = entPos.x, y = entPos.y, z = entPos.z}
                    }

                    pcall(function()
                        local displayName = entity:GetDisplayName()
                        if displayName and displayName ~= "" then
                            entry.name = displayName
                        end
                    end)

                    table.insert(entities, entry)
                    count = count + 1
                end
            end)
        end
    end)

    if not ok then
        return nil, "Failed to scan entities: " .. tostring(err)
    end

    return {radius = radius, count = count, entities = entities}
end

function handlers.set_time(args)
    if not args or not args.hours then
        return nil, "hours is required"
    end

    local hours = args.hours
    local minutes = args.minutes or 0
    local seconds = args.seconds or 0

    local ok, err = pcall(function()
        local timeSystem = Game.GetTimeSystem()
        local currentTime = timeSystem:GetGameTime()
        local currentDay = GameTime.Days(currentTime)

        local newTime = GameTime.MakeGameTime(currentDay, hours, minutes, seconds)
        local diff = newTime - currentTime

        if GameTime.IsNegative(diff) then
            diff = diff + GameTime.MakeGameTime(1, 0, 0, 0)
        end

        timeSystem:SetGameTimeBySeconds(GameTime.GetSeconds(currentTime) + GameTime.GetSeconds(diff))
    end)

    if not ok then
        return nil, "Failed to set time: " .. tostring(err)
    end

    return {hours = hours, minutes = minutes, seconds = seconds, success = true}
end

function handlers.set_weather(args)
    if not args or not args.weather then
        return nil, "weather is required"
    end

    local weatherMap = {
        Sunny = "24h_weather_sunny",
        Cloudy = "24h_weather_cloudy",
        Rain = "24h_weather_rain",
        HeavyRain = "24h_weather_heavy_rain",
        Fog = "24h_weather_fog",
        Toxic = "24h_weather_toxic_rain",
        Sandstorm = "24h_weather_sandstorm",
        Pollution = "24h_weather_pollution"
    }

    local weatherId = weatherMap[args.weather]
    if not weatherId then
        local valid = {}
        for k, _ in pairs(weatherMap) do
            table.insert(valid, k)
        end
        return nil, "Unknown weather: " .. tostring(args.weather) .. ". Valid: " .. table.concat(valid, ", ")
    end

    local ok, err = pcall(function()
        Game.GetWeatherSystem():SetWeather(weatherId, 5.0, 0)
    end)

    if not ok then
        return nil, "Failed to set weather: " .. tostring(err)
    end

    return {weather = args.weather, weatherId = weatherId, success = true}
end

-- Quest handlers

function handlers.get_quest_fact(args)
    if not args or not args.factName then
        return nil, "factName is required"
    end

    local ok, value = pcall(function()
        return Game.GetQuestsSystem():GetFact(CName.new(args.factName))
    end)

    if not ok then
        return nil, "Failed to get quest fact: " .. tostring(value)
    end

    return {factName = args.factName, value = value}
end

function handlers.set_quest_fact(args)
    if not args or not args.factName or args.value == nil then
        return nil, "factName and value are required"
    end

    local ok, err = pcall(function()
        Game.GetQuestsSystem():SetFact(CName.new(args.factName), args.value)
    end)

    if not ok then
        return nil, "Failed to set quest fact: " .. tostring(err)
    end

    return {factName = args.factName, value = args.value, success = true}
end

-- Extended player handlers

function handlers.get_active_effects()
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local effects = {}

    local ok, err = pcall(function()
        local ses = Game.GetStatusEffectSystem()
        local appliedEffects = ses:GetAppliedEffects(player:GetEntityID())

        if appliedEffects then
            for _, effect in ipairs(appliedEffects) do
                pcall(function()
                    local entry = {
                        id = TDBID.ToStringDEBUG(effect:GetRecord():GetID()),
                        remainingDuration = effect:GetRemainingDuration(),
                        stackCount = effect:GetStackCount()
                    }
                    table.insert(effects, entry)
                end)
            end
        end
    end)

    if not ok then
        return nil, "Failed to get effects: " .. tostring(err)
    end

    return {count = #effects, effects = effects}
end

function handlers.toggle_god_mode(args)
    if not args or args.enabled == nil then
        return nil, "enabled is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local ok, err = pcall(function()
        if args.enabled then
            Game.GetGodModeSystem():AddGodMode(player:GetEntityID(), gameGodModeType.Invulnerable, CName.new("CETBridge"))
        else
            Game.GetGodModeSystem():RemoveGodMode(player:GetEntityID(), gameGodModeType.Invulnerable, CName.new("CETBridge"))
        end
    end)

    if not ok then
        return nil, "Failed to toggle god mode: " .. tostring(err)
    end

    return {godMode = args.enabled, success = true}
end

function handlers.set_level(args)
    if not args then
        return nil, "level or streetCred is required"
    end

    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local results = {}

    if args.level then
        local ok, err = pcall(function()
            Game.SetLevel("Level", args.level, true)
        end)
        results.level = ok and args.level or ("Error: " .. tostring(err))
    end

    if args.streetCred then
        local ok, err = pcall(function()
            Game.SetLevel("StreetCred", args.streetCred, true)
        end)
        results.streetCred = ok and args.streetCred or ("Error: " .. tostring(err))
    end

    return results
end

function handlers.get_appearance_info()
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local info = {}

    pcall(function()
        local tpsSystem = Game.GetTransactionSystem()
        info.currentAppearance = NameToString(player:GetCurrentAppearanceName())
    end)

    pcall(function()
        info.className = NameToString(player:GetClassName())
    end)

    pcall(function()
        local gender = player:GetResolvedGenderName()
        if gender then
            info.gender = NameToString(gender)
        end
    end)

    return info
end

function handlers.get_vehicle_list()
    local vehicles = {}

    local ok, err = pcall(function()
        local vehicleSystem = Game.GetVehicleSystem()
        local playerVehicles = vehicleSystem:GetPlayerUnlockedVehicles()

        if playerVehicles then
            for _, vehicleData in ipairs(playerVehicles) do
                pcall(function()
                    local entry = {
                        id = TDBID.ToStringDEBUG(vehicleData.recordID)
                    }
                    pcall(function()
                        local record = TweakDB:GetRecord(vehicleData.recordID)
                        if record then
                            entry.name = Game.GetLocalizedText(record:DisplayName())
                        end
                    end)
                    table.insert(vehicles, entry)
                end)
            end
        end
    end)

    if not ok then
        return nil, "Failed to get vehicles: " .. tostring(err)
    end

    return {count = #vehicles, vehicles = vehicles}
end

-- Extended world handlers

function handlers.kill_nearby_npcs(args)
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local radius = (args and args.radius) or 30
    local allNpcs = args and args.allNpcs or false
    local killed = 0

    local ok, err = pcall(function()
        local searchQuery = Game['TSQ_NPC;']()
        searchQuery.maxDistance = radius

        local found = Game.GetTargetingSystem():GetTargetParts(player, searchQuery)
        if not found then return end

        for _, targetPart in ipairs(found) do
            pcall(function()
                local entity = targetPart:GetComponent():GetEntity()
                if not entity then return end

                local npc = entity
                local shouldKill = allNpcs

                if not shouldKill then
                    pcall(function()
                        local attAgent = npc:GetAttitudeAgent()
                        if attAgent then
                            local attitude = attAgent:GetAttitudeTowards(player:GetAttitudeAgent())
                            shouldKill = (attitude == EAIAttitude.AIA_Hostile)
                        end
                    end)
                end

                if shouldKill then
                    pcall(function()
                        npc:Kill(player, false, false)
                        killed = killed + 1
                    end)
                end
            end)
        end
    end)

    if not ok then
        return nil, "Failed to kill NPCs: " .. tostring(err)
    end

    return {killed = killed, radius = radius, allNpcs = allNpcs}
end

function handlers.show_notification(args)
    if not args or not args.message then
        return nil, "message is required"
    end

    local ok, err = pcall(function()
        local player = Game.GetPlayer()
        if player then
            player:SetWarningMessage(args.message)
        else
            error("Player not available")
        end
    end)

    if not ok then
        return nil, "Failed to show notification: " .. tostring(err)
    end

    return {message = args.message, shown = true}
end

function handlers.play_sound(args)
    if not args or not args.soundEvent then
        return nil, "soundEvent is required"
    end

    local ok, err = pcall(function()
        local player = Game.GetPlayer()
        if player then
            GameObject.PlaySoundEvent(player, CName.new(args.soundEvent))
        else
            error("Player not available")
        end
    end)

    if not ok then
        return nil, "Failed to play sound: " .. tostring(err)
    end

    return {soundEvent = args.soundEvent, played = true}
end

function handlers.get_scanner_info()
    local player = Game.GetPlayer()
    if not player then
        return nil, "Player not available"
    end

    local info = {}

    local ok, err = pcall(function()
        local targetingSystem = Game.GetTargetingSystem()
        local lookAtTarget = targetingSystem:GetLookAtObject(player)

        if not lookAtTarget then
            error("No target in view — look at an entity first")
        end

        info.className = NameToString(lookAtTarget:GetClassName())

        pcall(function()
            info.displayName = lookAtTarget:GetDisplayName()
        end)

        pcall(function()
            local pos = lookAtTarget:GetWorldPosition()
            info.position = {x = pos.x, y = pos.y, z = pos.z}
        end)

        -- NPC-specific info
        pcall(function()
            local npc = lookAtTarget
            local statsSystem = Game.GetStatsSystem()
            local entityID = npc:GetEntityID()

            info.level = math.floor(statsSystem:GetStatValue(entityID, gamedataStatType.Level))
            info.health = math.floor(statsSystem:GetStatValue(entityID, gamedataStatType.Health))

            pcall(function()
                local statPoolSystem = Game.GetStatPoolsSystem()
                info.currentHealth = math.floor(statPoolSystem:GetStatPoolValue(entityID, gamedataStatPoolType.Health))
            end)
        end)

        pcall(function()
            info.isNPC = lookAtTarget:IsNPC()
        end)

        pcall(function()
            info.isDead = lookAtTarget:IsDead()
        end)

        pcall(function()
            local record = TweakDB:GetRecord(lookAtTarget:GetRecordID())
            if record then
                info.recordId = TDBID.ToStringDEBUG(record:GetID())
            end
        end)
    end)

    if not ok then
        return nil, tostring(err)
    end

    return info
end

return handlers
