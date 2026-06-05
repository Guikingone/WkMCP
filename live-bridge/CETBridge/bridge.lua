local json = require("json")
local config = require("config")
local serializer = require("serializer")
local handlers = require("handlers")

local bridge = {
    initialized = false,
    heartbeatTimer = 0,
    socket = nil,
    tcpConnected = false
}

function bridge:Init()
    self.initialized = true

    if config.transport == "tcp" then
        local ok, err = pcall(function() self:InitTcp() end)
        if not ok then
            print("[CETBridge] TCP init failed: " .. tostring(err) .. ", falling back to file")
            config.transport = "file"
            self:WriteHeartbeat()
        end
    else
        self:WriteHeartbeat()
    end

    print("[CETBridge] Bridge initialized (transport: " .. config.transport .. ")")
end

function bridge:InitTcp()
    local ok, rsApi = pcall(GetMod, "RedSocket")
    if not ok or not rsApi then
        print("[CETBridge] RedSocket not found, falling back to file transport")
        config.transport = "file"
        self:WriteHeartbeat()
        return
    end

    self.redSocketApi = rsApi
    self.socket = rsApi.createSocket()

    self.socket:RegisterListener(
        -- onCommand: received data from MCP server
        function(data)
            local ok, request = pcall(json.decode, data)
            if not ok or not request then
                print("[CETBridge] Failed to parse TCP command: " .. tostring(request))
                return
            end

            local response = self:Execute(request)
            self:TcpSend(response)
        end,
        -- onConnection
        function(status)
            self.tcpConnected = true
            print("[CETBridge] TCP connected (status: " .. tostring(status) .. ")")
        end,
        -- onDisconnection
        function()
            self.tcpConnected = false
            print("[CETBridge] TCP disconnected")
        end,
        -- onError
        function()
            print("[CETBridge] TCP send failed (retries exhausted)")
        end
    )

    self.socket:Connect(config.tcp_host, config.tcp_port)
end

function bridge:TcpSend(data)
    if not self.socket or not self.tcpConnected then return end

    local ok, encoded = pcall(json.encode, data)
    if not ok then
        local fok, fallback = pcall(json.encode, {
            id = (type(data) == "table" and data.id) or "unknown",
            ok = false,
            error = "Failed to encode response"
        })
        if fok then
            self.socket:SendCommand(fallback)
        end
        return
    end

    self.socket:SendCommand(encoded)
end

function bridge:Update(dt)
    if not self.initialized then return end

    -- Heartbeat
    self.heartbeatTimer = self.heartbeatTimer + dt
    if self.heartbeatTimer >= config.heartbeat_interval then
        self.heartbeatTimer = 0

        if config.transport == "tcp" then
            self:TcpHeartbeat()
        end
        -- Always write file heartbeat so file-based sessions can detect us
        self:WriteHeartbeat()
    end

    -- Always poll for file commands (supports multiple sessions)
    self:PollFileCommand()
end

function bridge:TcpHeartbeat()
    if self.tcpConnected and self.socket then
        self:TcpSend({
            type = "heartbeat",
            timestamp = os.date("!%Y-%m-%dT%H:%M:%SZ"),
            version = "0.1.0"
        })
    end
end

function bridge:PollFileCommand()
    local cmdPath = "command.json"
    local file = io.open(cmdPath, "r")
    if not file then return end

    local content = file:read("*a")
    file:close()

    os.remove(cmdPath)

    if not content or content == "" then return end

    local ok, request = pcall(json.decode, content)
    if not ok or not request then
        print("[CETBridge] Failed to parse command: " .. tostring(request))
        return
    end

    local response = self:Execute(request)

    local resOk, resJson = pcall(json.encode, response)
    if not resOk then
        response = {id = request.id, ok = false, error = "Failed to encode response: " .. tostring(resJson)}
        resJson = json.encode(response)
    end

    local tmpPath = "response.json.tmp"
    local resPath = "response.json"
    local tmpFile = io.open(tmpPath, "w")
    if tmpFile then
        tmpFile:write(resJson)
        tmpFile:close()
        os.rename(tmpPath, resPath)
    end
end

function bridge:Execute(request)
    local id = request.id or "unknown"
    local reqType = request.type

    if reqType == "exec" then
        return self:ExecCode(id, request.code)
    elseif reqType == "eval" then
        return self:EvalExpr(id, request.expr)
    elseif reqType == "query" then
        return self:RunQuery(id, request.handler, request.args)
    else
        return {id = id, ok = false, error = "Unknown request type: " .. tostring(reqType)}
    end
end

function bridge:ExecCode(id, code)
    if not code then
        return {id = id, ok = false, error = "No code provided"}
    end

    local output = {}
    local originalPrint = print
    print = function(...)
        local args = {...}
        local parts = {}
        for i = 1, select("#", ...) do
            parts[i] = tostring(args[i])
        end
        local line = table.concat(parts, "\t")
        table.insert(output, line)
        originalPrint(...)
    end

    local fn, loadErr = loadstring(code)
    if not fn then
        print = originalPrint
        return {id = id, ok = false, error = "Syntax error: " .. tostring(loadErr)}
    end

    local ok, execErr = pcall(fn)
    print = originalPrint

    if not ok then
        return {id = id, ok = false, error = "Runtime error: " .. tostring(execErr)}
    end

    local resultText = table.concat(output, "\n")
    return {id = id, ok = true, result = resultText}
end

function bridge:EvalExpr(id, expr)
    if not expr then
        return {id = id, ok = false, error = "No expression provided"}
    end

    local fn, loadErr = loadstring("return " .. expr)
    if not fn then
        return {id = id, ok = false, error = "Syntax error: " .. tostring(loadErr)}
    end

    local ok, result = pcall(fn)
    if not ok then
        return {id = id, ok = false, error = "Runtime error: " .. tostring(result)}
    end

    local serialized = serializer.serialize(result)
    return {id = id, ok = true, result = serialized}
end

function bridge:RunQuery(id, handlerName, args)
    if not handlerName then
        return {id = id, ok = false, error = "No handler specified"}
    end

    local handler = handlers[handlerName]
    if not handler then
        return {id = id, ok = false, error = "Unknown handler: " .. tostring(handlerName)}
    end

    local ok, result, err = pcall(handler, args)
    if not ok then
        return {id = id, ok = false, error = "Handler error: " .. tostring(result)}
    end

    if err then
        return {id = id, ok = false, error = err}
    end

    local serialized = serializer.serialize(result)
    return {id = id, ok = true, result = serialized}
end

function bridge:WriteHeartbeat()
    local hbPath = "heartbeat.json"
    local data = json.encode({
        timestamp = os.date("!%Y-%m-%dT%H:%M:%SZ"),
        mod = "CETBridge",
        version = "0.1.0"
    })
    local file = io.open(hbPath, "w")
    if file then
        file:write(data)
        file:close()
    end
end

function bridge:Shutdown()
    self.initialized = false

    if config.transport == "tcp" and self.socket then
        pcall(function() self.socket:Disconnect() end)
        if self.redSocketApi then
            pcall(function() self.redSocketApi.destroySocket(self.socket) end)
        end
        self.socket = nil
        self.tcpConnected = false
    end

    pcall(os.remove, "heartbeat.json")
    pcall(os.remove, "command.json")
    pcall(os.remove, "response.json")
    print("[CETBridge] Bridge shut down")
end

return bridge
