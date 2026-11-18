using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Mount ("Riding Imagine"/vehicle) control through the game's `vehicle` view-model:
/// <c>AsyncStartRide(useType, token)</c> mounts the equipped mount, <c>StopRide()</c> dismounts.
/// VehicleUseType: land=11, water=12, landAndWater=13.
/// </summary>
public sealed class MountService
{
    private const int UseTypeLand = 11;
    private const int UseTypeWater = 12;
    private const int UseTypeLandAndWater = 13;

    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;

    public MountService(IPluginServices services, LuaBridge lua)
    {
        _services = services;
        _lua = lua;
    }

    /// <param name="p">action = mount | dismount | toggle (default mount); type = auto | land | water</param>
    public JsonObject Execute(JsonObject? p)
    {
        string action = (p?["action"]?.GetValue<string>() ?? "mount").Trim().ToLowerInvariant();
        string type = (p?["type"]?.GetValue<string>() ?? "auto").Trim().ToLowerInvariant();

        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        return action switch
        {
            "mount" or "ride" or "on" => Mount(type),
            "dismount" or "off" or "stop" or "unride" => Dismount(),
            "toggle" => IsRiding() ? Dismount() : Mount(type),
            _ => new JsonObject { ["ok"] = false, ["error"] = $"Unknown ride action '{action}' (mount, dismount, toggle)" }
        };
    }

    public bool IsRiding()
    {
        var res = _lua.Eval("local e = Z.EntityMgr.PlayerEnt return e ~= nil and (e:GetLuaRidingId() or 0) ~= 0");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public JsonObject Dismount()
    {
        var res = _lua.Eval(@"
local vvm = Z.VMMgr.GetVM('vehicle')
local ent = Z.EntityMgr.PlayerEnt
if not vvm or not ent then return { ok = false, error = 'vehicle_vm unavailable' } end
if (ent:GetLuaRidingId() or 0) == 0 then return { ok = true, already_dismounted = true } end
local okS, errS = pcall(function() vvm.StopRide() end)
return { ok = okS, action = 'dismount', error = okS and nil or tostring(errS) }
");
        return UnwrapEval(res);
    }

    public JsonObject Mount(string type)
    {
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local wanted = {LuaLiteral.Quote(type)}\nlocal LAND, WATER, BOTH = {UseTypeLand}, {UseTypeWater}, {UseTypeLandAndWater}\n");

        const string body = @"
local vvm = Z.VMMgr.GetVM('vehicle')
local ent = Z.EntityMgr.PlayerEnt
if not vvm or not ent then return { ok = false, error = 'vehicle_vm unavailable' } end

local dvm = Z.VMMgr.GetVM('dead')
if dvm and dvm.CheckPlayerIsDead() then return { ok = false, error = 'dead' } end
if (ent:GetLuaRidingId() or 0) ~= 0 then return { ok = true, already_riding = true } end

local function equipped(t)
  local okE, v = pcall(function() return vvm.IsTypeEquip(t) end)
  return okE and type(v) == 'number' and v > 0
end

local useType
if wanted == 'land' then useType = LAND
elseif wanted == 'water' then useType = WATER
else
  if equipped(LAND) then useType = LAND
  elseif equipped(BOTH) then useType = BOTH
  elseif equipped(WATER) then useType = WATER end
end
if not useType or not equipped(useType) then
  return { ok = false, error = 'no equipped mount for requested type', requested = wanted }
end

local cs = Z.CancelSource.Rent()
local okR, errR = pcall(function() vvm.AsyncStartRide(useType, cs:CreateToken()) end)
cs:Recycle()
local riding = (ent:GetLuaRidingId() or 0) ~= 0
return { ok = okR, action = 'mount', use_type = useType, riding = riding, error = okR and nil or tostring(errR) }
";
        string? asyncId = _lua.StartCoroutineRpc(header + body);
        return new JsonObject
        {
            ["ok"] = asyncId != null,
            ["async_id"] = asyncId,
            ["action"] = "mount"
        };
    }

    private static JsonObject UnwrapEval(JsonObject evalRes)
    {
        if (evalRes["ok"]?.GetValue<bool>() == true && evalRes["result"] is JsonObject inner)
        {
            var copy = JsonNode.Parse(inner.ToJsonString())!.AsObject();
            return copy;
        }
        return evalRes;
    }
}
