using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters.Combat;

public sealed class LuaCombatAdapter : ICombatAdapter
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    public string Domain => "combat";
    public bool IsLuaReady => _lua.IsReady;

    public LuaCombatAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
    {
        _services = services;
        _lua = lua;
        _guard = guard;
    }

    public IReadOnlyList<CapabilityEntry> ProbeLoadTime()
    {
        return new[]
        {
            CapabilityProbeHelper.ProbeNativeMultiTypes(
                CapabilityKeys.CombatEvade,
                Domain,
                new[] { "Panda.ZGame.ZWarningEffectShowInfo", "Panda.ZGame.ZUISkillWarningShowInfo" })
        };
    }

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        const string specReturn = @"
return {
  revive = evalSpec({
    function() return hasVm('dead', {'CheckPlayerIsDead', 'GetCurReviveIdList', 'AsyncRevive'}) end,
    function() return hasTbl('ReviveTableMgr') end
  }, 1),
  auto_combat = evalSpec({
    function() return hasTbl('SceneTableMgr') end
  }, 1)
}
";
        var checks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);
        return new[]
        {
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.Revive, Domain),
            CapabilityProbeHelper.ParseLuaCheck(checks, CapabilityKeys.AutoCombat, Domain)
        };
    }

    public bool ReadIsPlayerDead()
    {
        var res = _lua.Eval("local d = Z.VMMgr.GetVM('dead') return d ~= nil and d.CheckPlayerIsDead() == true");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public string? StartReviveCoroutine(int[] typePriority, bool dryRun)
    {
        if (!_lua.IsReady)
            return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local prio = {{{string.Join(",", typePriority)}}}\nlocal dryRun = {LuaLiteral.Bool(dryRun)}\n");

        const string body = @"
local dvm = Z.VMMgr.GetVM('dead')
if not dvm then return { ok = false, error = 'dead_vm unavailable' } end
if not dvm.CheckPlayerIsDead() then return { ok = true, already_alive = true } end

local ids = dvm.GetCurReviveIdList()
if type(ids) ~= 'table' then return { ok = false, error = 'no revive options' } end

local best, bestRank, bestType
local options = {}
for _, id in pairs(ids) do
  local okRow, row = pcall(function() return Z.TableMgr.GetRow('ReviveTableMgr', id, true) end)
  if okRow and row then
    local free = true
    if type(row.Consume) == 'table' and next(row.Consume) ~= nil then free = false end
    local countOk = true
    local okC, c = pcall(function() return dvm.CheckReviveCount(id, false) end)
    if okC and c == false then countOk = false end
    local rank
    for i, t in ipairs(prio) do if t == row.Type then rank = i break end end
    options[#options + 1] = { id = id, type = row.Type, free = free, count_ok = countOk, rank = rank }
    if free and countOk and rank and (not bestRank or rank < bestRank) then
      best, bestRank, bestType = id, rank, row.Type
    end
  end
end
if not best then return { ok = false, error = 'no free revive option available', options = options } end
if dryRun then return { ok = true, dry_run = true, revive_id = best, type = bestType, options = options } end

local cs = Z.CancelSource.Rent()
local okR, ret = pcall(function() return dvm.AsyncRevive(best, cs:CreateToken()) end)
cs:Recycle()
return { ok = okR, revive_id = best, type = bestType, ret = tostring(ret), error = okR and nil or tostring(ret) }
";
        return _lua.StartCoroutineRpc(header + body);
    }

    public JsonObject PollAsync(string asyncId, bool clearWhenDone = true)
        => _lua.PollCoroutineRpc(asyncId, clearWhenDone);

    public bool IsNonCombatSafeScene(int sceneId)
    {
        if (sceneId <= 0 || !_lua.IsReady) return false;
        if (sceneId == 12000) return true;
        var res = _lua.Eval($@"
local stm = Z.TableMgr and Z.TableMgr.GetTable('SceneTableMgr')
if not stm or not stm.GetRow then return false end
local ok, row = pcall(function() return stm.GetRow({sceneId}) end)
if not ok or not row then return false end
local st = row.SceneSubType
return (st == 3 or st == 7 or st == 8)
");
        return res["ok"]?.GetValue<bool>() == true && res["result"]?.GetValue<bool>() == true;
    }

    public (bool ShareSuppressed, int RedFlashSeq) PollShareAndRedFlash()
    {
        if (!_lua.IsReady)
            return (false, -1);

        var res = _lua.Eval(@"
if not rawget(_G, '__agyShareHookInstalled') and Z and Z.TipsVM then
  local mtm = Z.TableMgr and Z.TableMgr.GetTable('MessageTableMgr')
  local function inspectMsg(a, b, c)
    local hit = false
    for _, arg in ipairs({a, b, c}) do
      if type(arg) == 'number' and arg > 0 and mtm and mtm.GetRow then
        local okR, row = pcall(function() return mtm.GetRow(arg, true) end)
        if okR and row and row.Content then
          local txt = tostring(row.Content)
          if txt:find('분담', 1, true) or txt:find('[집합]', 1, true) or txt:find('합류하', 1, true) or txt:find('집결', 1, true) then
            hit = true
            break
          end
        end
      elseif type(arg) == 'string' then
        if arg:find('분담', 1, true) or arg:find('[집합]', 1, true) or arg:find('합류하', 1, true) then
          hit = true
          break
        end
      end
    end
    if hit then
      rawset(_G, '__agyShareUntil', os.clock() + 7.0)
    end
  end

  if type(Z.TipsVM.ShowTips) == 'function' then
    local origShow = Z.TipsVM.ShowTips
    Z.TipsVM.ShowTips = function(...)
      pcall(inspectMsg, ...)
      return origShow(...)
    end
  end
  if type(Z.TipsVM.OpenGameBroadcast) == 'function' then
    local origBc = Z.TipsVM.OpenGameBroadcast
    Z.TipsVM.OpenGameBroadcast = function(...)
      pcall(inspectMsg, ...)
      return origBc(...)
    end
  end
  rawset(_G, '__agyShareHookInstalled', true)
end

if not rawget(_G, '__agyFlashHookInstalled') and Z and Z.EventMgr and type(Z.EventMgr.Dispatch) == 'function' then
  local warnEvt = Z.ConstValue and Z.ConstValue.Battle and Z.ConstValue.Battle.ShowSkillWarning
  local origDisp = Z.EventMgr.Dispatch
  Z.EventMgr.Dispatch = function(mgr, evName, ...)
    pcall(function()
      if (warnEvt and evName == warnEvt) or (type(evName) == 'string' and evName:find('SkillWarning', 1, true)) then
        rawset(_G, '__agyRedFlashSeq', (tonumber(rawget(_G, '__agyRedFlashSeq')) or 0) + 1)
      end
    end)
    return origDisp(mgr, evName, ...)
  end
  rawset(_G, '__agyFlashHookInstalled', true)
end

return {
  share = os.clock() < (tonumber(rawget(_G, '__agyShareUntil') or 0) or 0),
  flashSeq = tonumber(rawget(_G, '__agyRedFlashSeq') or 0) or 0
}
");
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj)
        {
            bool share = obj["share"]?.GetValue<bool>() == true;
            int seq = obj["flashSeq"]?.GetValue<int>() ?? -1;
            return (share, seq);
        }
        return (false, -1);
    }
}
