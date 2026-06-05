local serializer = {}

function serializer.serialize(value, depth)
    depth = depth or 0
    if depth > 10 then return '"[max depth]"' end

    local t = type(value)

    if t == "nil" then
        return "null"
    elseif t == "boolean" then
        return tostring(value)
    elseif t == "number" then
        if value ~= value then return '"NaN"' end
        if value == math.huge then return '"Infinity"' end
        if value == -math.huge then return '"-Infinity"' end
        return tostring(value)
    elseif t == "string" then
        return serializer.escapeString(value)
    elseif t == "table" then
        return serializer.serializeTable(value, depth)
    elseif t == "userdata" then
        return serializer.serializeUserdata(value, depth)
    else
        return '"[' .. t .. ']"'
    end
end

function serializer.escapeString(s)
    s = s:gsub('\\', '\\\\')
    s = s:gsub('"', '\\"')
    s = s:gsub('\n', '\\n')
    s = s:gsub('\r', '\\r')
    s = s:gsub('\t', '\\t')
    return '"' .. s .. '"'
end

function serializer.serializeTable(tbl, depth)
    local isArray = true
    local maxIndex = 0
    for k, _ in pairs(tbl) do
        if type(k) == "number" and k > 0 and math.floor(k) == k then
            if k > maxIndex then maxIndex = k end
        else
            isArray = false
            break
        end
    end

    if isArray and maxIndex > 0 then
        local parts = {}
        for i = 1, maxIndex do
            parts[i] = serializer.serialize(tbl[i], depth + 1)
        end
        return "[" .. table.concat(parts, ",") .. "]"
    end

    local parts = {}
    for k, v in pairs(tbl) do
        local key = type(k) == "string" and k or tostring(k)
        table.insert(parts, serializer.escapeString(key) .. ":" .. serializer.serialize(v, depth + 1))
    end
    return "{" .. table.concat(parts, ",") .. "}"
end

function serializer.serializeUserdata(value, depth)
    local ok, result

    -- Try CName (common in CP2077)
    ok, result = pcall(function() return NameToString(value) end)
    if ok and result then
        return serializer.escapeString(result)
    end

    -- Try TweakDBID
    ok, result = pcall(function() return TDBID.ToStringDEBUG(value) end)
    if ok and result then
        return serializer.escapeString(result)
    end

    -- Try Vector4
    ok, result = pcall(function()
        return {x = value.x, y = value.y, z = value.z, w = value.w}
    end)
    if ok and result and result.x then
        return serializer.serializeTable(result, depth + 1)
    end

    -- Try Quaternion
    ok, result = pcall(function()
        return {i = value.i, j = value.j, k = value.k, r = value.r}
    end)
    if ok and result and result.i then
        return serializer.serializeTable(result, depth + 1)
    end

    -- Fallback: tostring
    ok, result = pcall(tostring, value)
    if ok then
        return serializer.escapeString(result)
    end

    return '"[userdata]"'
end

return serializer
