using System;
using System.Text.Json.Nodes;
using System.Threading;
using Stellar.Abstractions.Services;

namespace Stellar.AgyBridge.Lua;

/// <summary>
/// Safe Unity-main-thread wrapper over Stellar's ILua service.
/// Installs a lightweight pure-Lua JSON encoder into _G.__agyJson once Lua is Ready,
/// allowing arbitrary Lua queries and coroutine RPCs to return structured JSON without native callbacks.
/// </summary>
public sealed class LuaBridge
{
    private readonly IPluginServices _services;
    private bool _bootstrapInstalled;
    private long _seq;

    private const string OutKey = "__agyBridgeOut";

    private const string BootstrapLua = @"
pcall(function()
  if rawget(_G, '__agyBridgeBoot') then return end
  rawset(_G, '__agyBridgeAsync', {})

  local function esc(s)
    return (tostring(s):gsub('\\', '\\\\'):gsub('""', '\\""'):gsub('\n', '\\n'):gsub('\r', '\\r'):gsub('\t', '\\t'))
  end

  local function enc(v, depth)
    depth = (depth or 0) + 1
    if depth > 6 then return '""<max_depth>""' end
    local t = type(v)
    if v == nil then return 'null' end
    if t == 'boolean' then return v and 'true' or 'false' end
    if t == 'number' then
      if v ~= v or v == math.huge or v == -math.huge then return 'null' end
      return tostring(v)
    end
    if t == 'string' then return '""' .. esc(v) .. '""' end
    if t ~= 'table' then return '""<' .. t .. '>""' end

    local isArr = true
    local maxIdx = 0
    local count = 0
    for k, _ in pairs(v) do
      count = count + 1
      if type(k) ~= 'number' or k < 1 or math.floor(k) ~= k then
        isArr = false
        break
      end
      if k > maxIdx then maxIdx = k end
    end
    if count == 0 then return '{}' end
    if isArr and maxIdx <= count * 2 then
      local parts = {}
      for i = 1, maxIdx do
        parts[#parts + 1] = enc(v[i], depth)
      end
      return '[' .. table.concat(parts, ',') .. ']'
    end

    local parts = {}
    local limit = 0
    for k, val in pairs(v) do
      limit = limit + 1
      if limit > 200 then
        parts[#parts + 1] = '""__truncated__"":true'
        break
      end
      parts[#parts + 1] = '""' .. esc(tostring(k)) .. '"":' .. enc(val, depth)
    end
    return '{' .. table.concat(parts, ',') .. '}'
  end

  rawset(_G, '__agyJsonEnc', enc)
  rawset(_G, '__agyBridgeBoot', true)
end)
";

    public LuaBridge(IPluginServices services)
    {
        _services = services;
    }

    public bool IsReady => _services.Lua.Ready;

    private bool EnsureBootstrap()
    {
        if (!_services.Lua.Ready)
        {
            _bootstrapInstalled = false;
            return false;
        }
        if (_bootstrapInstalled && _services.Lua.TryReadGlobalBool("__agyBridgeBoot", out var booted) && booted)
            return true;

        _services.Lua.DoString(BootstrapLua);
        _bootstrapInstalled = _services.Lua.TryReadGlobalBool("__agyBridgeBoot", out var ok) && ok;
        return _bootstrapInstalled;
    }

    /// <summary>
    /// Evaluates a Lua function body (which may end with `return ...`) on the main thread
    /// and returns `{ ok: bool, result: ..., error?: string }`.
    /// </summary>
    public JsonObject Eval(string luaBody)
    {
        if (!EnsureBootstrap())
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "Lua state is not ready yet (pre-login or loading)."
            };
        }

        string wrapped = $@"
rawset(_G, '{OutKey}', nil)
local __ok, __res = pcall(function()
{luaBody}
end)
local __enc = rawget(_G, '__agyJsonEnc')
if __ok then
  rawset(_G, '{OutKey}', '{{""ok"":true,""result"":' .. (__enc and __enc(__res, 0) or 'null') .. '}}')
else
  local __err = tostring(__res):gsub('\\', '\\\\'):gsub('""', '\\""'):gsub('\n', ' ')
  rawset(_G, '{OutKey}', '{{""ok"":false,""error"":""' .. __err .. '""}}')
end
";
        _services.Lua.DoString(wrapped);
        var raw = _services.Lua.ReadGlobalString(OutKey);
        if (string.IsNullOrEmpty(raw))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "Lua evaluation produced no output."
            };
        }

        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject { ["ok"] = true, ["raw"] = raw };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"JSON parse error: {ex.Message}",
                ["raw"] = raw
            };
        }
    }

    /// <summary>
    /// Starts an asynchronous Lua coroutine via Z.CoroUtil.create_coro_xpcall and returns a request key
    /// whose status can be polled via PollAsync(reqKey).
    /// Inside coroBody, `return <value>` marks the async operation complete.
    /// </summary>
    public string? StartCoroutineRpc(string coroBody)
    {
        if (!EnsureBootstrap()) return null;
        var id = $"req_{Interlocked.Increment(ref _seq)}";
        string chunk = $@"
pcall(function()
  local A = rawget(_G, '__agyBridgeAsync')
  if not A then A = {{}} rawset(_G, '__agyBridgeAsync', A) end
  A['{id}'] = {{ state = 'running' }}
  Z.CoroUtil.create_coro_xpcall(function()
    local res = (function()
{coroBody}
    end)()
    A['{id}'] = {{ state = 'done', result = res }}
  end, function(err)
    local es = tostring(err or '')
    if es:find('ZCancelException', 1, true) or es:find('operation was canceled', 1, true) then
      A['{id}'] = {{ state = 'done', result = {{ ok = true, canceled_by_scene_unload = true }} }}
    else
      A['{id}'] = {{ state = 'error', error = es }}
    end
  end)()
end)
";
        _services.Lua.DoString(chunk);
        return id;
    }

    public JsonObject PollCoroutineRpc(string reqId, bool clearWhenDone = true)
    {
        string clearSnippet = clearWhenDone
            ? $"if entry and entry.state ~= 'running' then A['{reqId}'] = nil end"
            : "";
        return Eval($@"
local A = rawget(_G, '__agyBridgeAsync')
if not A then return {{ state = 'missing' }} end
local entry = A['{reqId}']
if not entry then return {{ state = 'missing' }} end
{clearSnippet}
return entry
");
    }
}
