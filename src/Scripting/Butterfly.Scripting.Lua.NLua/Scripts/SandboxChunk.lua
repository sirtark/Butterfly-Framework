local stop, allowed = ...
local G, env = _G, {}
local sethook, raise, rawget, type = G.debug.sethook, G.error, G.rawget, G.type
    
local function hook()
  if stop() then
    sethook(hook, "", 1)
    raise("The script was stopped.", 0)
  end
end

local function hooked(f, name)
  if type(f) ~= "function" then raise("bad argument #1 to '" .. name .. "' (function expected, got " .. type(f) .. ")", 3) end
  return function(...)
    sethook(hook, "", 1000)
    return f(...)
  end
end

local function copy(target, source, names)
  for name in names:gmatch("%S+") do target[name] = source[name] end
end
    
if allowed.base then
  copy(env, G, "assert collectgarbage error getmetatable ipairs next pairs pcall print rawequal rawget rawlen rawset select tonumber tostring type warn _VERSION")
  -- Lua suspends hooks while a __gc finalizer runs, so a finalizer could never be stopped.
  
  local setmetatable = G.setmetatable
  env.setmetatable = function(t, mt)
    if type(mt) == "table" and rawget(mt, "__gc") ~= nil then raise("__gc metamethods are not allowed", 2) end
    return setmetatable(t, mt)
  end

  -- An error raised by the hook reaches the message handler while hooks are still off, so a stopped script skips it.
  local xpcall = G.xpcall
  env.xpcall = function(f, handler, ...)
    if type(handler) ~= "function" then raise("bad argument #2 to 'xpcall' (function expected, got " .. type(handler) .. ")", 2) end
    return xpcall(f, function(message)
      if stop() then return message end
      return handler(message)
    end, ...)
  end
end
if allowed.string then env.string, env.utf8 = G.string, G.utf8 end
if allowed.table then env.table = G.table end
if allowed.math then env.math = G.math end
if allowed.coroutine then -- Hooks belong to a single thread, so every coroutine installs its own.
  local coroutine = G.coroutine
  env.coroutine = {}
  copy(env.coroutine, coroutine, "close isyieldable resume running status yield")
  env.coroutine.create = function(f) return coroutine.create(hooked(f, "create")) end
  env.coroutine.wrap = function(f) return coroutine.wrap(hooked(f, "wrap")) end
end
if allowed.ostime or allowed.ossystem then
  env.os = {}
  if allowed.ostime then copy(env.os, G.os, "clock date difftime time") end
  if allowed.ossystem then copy(env.os, G.os, "execute exit getenv remove rename setlocale tmpname") end
end
if allowed.load then
  local load = G.load
  env.load = function(chunk, name, _, e) return load(chunk, name, "t", e or env) end
end
if allowed.modules then copy(env, G, "dofile loadfile package require") end
if allowed.io then env.io = G.io end
if allowed.debug then env.debug = G.debug end
env._G = env

sethook(hook, "", 1000)
return env