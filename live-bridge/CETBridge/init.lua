local ok, bridge = pcall(require, "bridge")
if not ok then
    print("[CETBridge] FATAL: failed to load bridge module: " .. tostring(bridge))
    return
end

registerForEvent("onInit", function()
    local initOk, err = pcall(function() bridge:Init() end)
    if initOk then
        print("[CETBridge] Ready")
    else
        print("[CETBridge] Init failed: " .. tostring(err))
    end
end)

registerForEvent("onUpdate", function(dt)
    pcall(function() bridge:Update(dt) end)
end)

registerForEvent("onShutdown", function()
    pcall(function() bridge:Shutdown() end)
end)
