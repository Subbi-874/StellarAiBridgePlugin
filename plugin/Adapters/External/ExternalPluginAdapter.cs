using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Adapters.External;

public sealed class ExternalPluginAdapter : IExternalPluginAdapter
{
    private const string AutoGatherPluginTypeName = "Stellar.AutoGather.Plugin";
    private const string AutoFishingPluginTypeName = "Stellar.AutoFishing.Plugin";

    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _guard;

    // ─── AutoGather reflection & Harmony hook state ───
    private object? _gatherPluginInstance;
    private object? _gatherControllerInstance;
    private MethodInfo? _miSetAutoGatherEnabled;
    private MethodInfo? _miSetAutoGatherFocus;
    private FieldInfo? _fiGatherEnabled;
    private FieldInfo? _fiLockedConfigId;
    private FieldInfo? _fiLockedName;
    private PropertyInfo? _piGatherStatus;
    private PropertyInfo? _piGatherFocusMode;

    private static long _completedGathers;
    private static string _lastGatherCompletionVia = "";
    private Harmony? _harmony;
    private bool _completionHooked;
    private bool _completionHookTried;

    // ─── AutoFishing reflection state ───
    private object? _fishPluginInstance;
    private MethodInfo? _miStartAuto;
    private MethodInfo? _miStopAuto;
    private PropertyInfo? _piFishState;
    private FieldInfo? _fiAutoEnabled;
    private FieldInfo? _fiFishCaught;
    private FieldInfo? _fiFishMissed;
    private FieldInfo? _fiRodChanged;

    public string Domain => "external";
    public bool IsLuaReady => _lua.IsReady;

    public ExternalPluginAdapter(IPluginServices services, LuaBridge lua, WorldAccessGuard guard)
    {
        _services = services;
        _lua = lua;
        _guard = guard;
    }

    // ─────────────────────── Capabilities ───────────────────────

    public IReadOnlyList<CapabilityEntry> ProbeLoadTime()
    {
        return new[]
        {
            ProbeExternalPluginMetadata(
                CapabilityKeys.AutoGather,
                "gathering",
                AutoGatherPluginTypeName,
                new[] { "_autoGatherController", "SetAutoGatherEnabled", "SetAutoGatherFocus" },
                inWorld: false),
            ProbeExternalPluginMetadata(
                CapabilityKeys.AutoFish,
                "fishing",
                AutoFishingPluginTypeName,
                new[] { "StartAuto", "StopAuto", "_autoEnabled", "_fishCaught", "_fishMissed", "_rodChanged" },
                inWorld: false)
        };
    }

    public IReadOnlyList<CapabilityEntry> ProbeInWorld()
    {
        if (!_guard.IsLuaWorldReady)
            return Array.Empty<CapabilityEntry>();

        const string specReturn = @"
return {
  auto_gather = evalSpec({
    function() return hasTbl('LifeCollectListTableMgr') end,
    function() return hasTbl('CollectionTableMgr') end
  }, 1),
  auto_fish = evalSpec({
    function() return hasVm('fishing') end,
    function() return hasTbl('FishingAreaTableMgr') end,
    function() return hasTbl('FishingTableMgr') end
  }, 1)
}
";
        var luaChecks = CapabilityProbeHelper.EvalLuaSpecs(_lua, _services.Log, Domain, specReturn);

        var gatherPlugin = ProbeExternalPluginMetadata(
            CapabilityKeys.AutoGather,
            "gathering",
            AutoGatherPluginTypeName,
            new[] { "_autoGatherController", "SetAutoGatherEnabled", "SetAutoGatherFocus" },
            inWorld: true);

        var fishPlugin = ProbeExternalPluginMetadata(
            CapabilityKeys.AutoFish,
            "fishing",
            AutoFishingPluginTypeName,
            new[] { "StartAuto", "StopAuto", "_autoEnabled", "_fishCaught", "_fishMissed", "_rodChanged" },
            inWorld: true);

        return new[]
        {
            CombinePluginWithLua(gatherPlugin, luaChecks, CapabilityKeys.AutoGather, "gathering"),
            CombinePluginWithLua(fishPlugin, luaChecks, CapabilityKeys.AutoFish, "fishing")
        };
    }

    private CapabilityEntry ProbeExternalPluginMetadata(
        string key,
        string domain,
        string pluginTypeFullName,
        string[] requiredMembers,
        bool inWorld)
    {
        string label = CapabilityKeys.GetLabel(key);
        // Never touch IL2CPPChainloader or game singletons here — ProbeLoadTime relies purely on CLR type metadata & launcher entries.
        Type? type = InteropResolver.FindType(pluginTypeFullName)
            ?? TryFindViaLauncherEntries(pluginTypeFullName)?.GetType();

        if (type == null)
        {
            return inWorld
                ? new CapabilityEntry(key, label, domain, CapabilityState.Missing, $"{pluginTypeFullName} 미설치 또는 비활성")
                : new CapabilityEntry(key, label, domain, CapabilityState.Unknown, "플러그인 로드 순서 대기 중");
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var missing = new List<string>();
        foreach (var memberName in requiredMembers)
        {
            if (type.GetMember(memberName, flags).Length == 0)
                missing.Add(memberName);
        }

        if (missing.Count == 0)
            return new CapabilityEntry(key, label, domain, CapabilityState.Available, pluginTypeFullName);

        bool primaryMissing = Array.Exists(missing.ToArray(), m =>
            m is "SetAutoGatherEnabled" or "_autoGatherController" or "StartAuto" or "StopAuto");
        return new CapabilityEntry(
            key,
            label,
            domain,
            primaryMissing ? CapabilityState.Missing : CapabilityState.Degraded,
            $"{pluginTypeFullName} missing: {string.Join(", ", missing)}");
    }

    private static CapabilityEntry CombinePluginWithLua(
        CapabilityEntry pluginEntry,
        JsonObject? luaChecks,
        string key,
        string domain)
    {
        if (pluginEntry.State == CapabilityState.Missing)
            return pluginEntry;

        var luaEntry = CapabilityProbeHelper.ParseLuaCheck(luaChecks, key, domain);
        if (luaEntry.State == CapabilityState.Missing || luaEntry.State == CapabilityState.Degraded || pluginEntry.State == CapabilityState.Degraded)
        {
            return pluginEntry with
            {
                State = CapabilityState.Degraded,
                Detail = $"{pluginEntry.Detail}; Lua: {luaEntry.Detail}"
            };
        }
        return pluginEntry with { Detail = $"{pluginEntry.Detail}; {luaEntry.Detail}" };
    }

    // ─────────────────────── Shared Companion Plugin Discovery ───────────────────────

    private object? TryFindPluginInstance(string targetTypeFullName)
        => TryFindViaLauncherEntries(targetTypeFullName) ?? TryFindViaChainloaderRegistry(targetTypeFullName);

    private object? TryFindViaLauncherEntries(string targetTypeFullName)
    {
        try
        {
            foreach (var entry in _services.Launcher.Entries)
            {
                if (entry == null) continue;
                var eType = entry.GetType();
                foreach (var prop in eType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!typeof(Delegate).IsAssignableFrom(prop.PropertyType)) continue;
                    var target = (prop.GetValue(entry) as Delegate)?.Target;
                    if (target != null && target.GetType().FullName == targetTypeFullName)
                        return target;
                }
                foreach (var fld in eType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!typeof(Delegate).IsAssignableFrom(fld.FieldType)) continue;
                    var target = (fld.GetValue(entry) as Delegate)?.Target;
                    if (target != null && target.GetType().FullName == targetTypeFullName)
                        return target;
                }
            }
        }
        catch { }
        return null;
    }

    private static object? TryFindViaChainloaderRegistry(string targetTypeFullName)
    {
        try
        {
            var chainType = InteropResolver.FindType("BepInEx.Unity.IL2CPP.IL2CPPChainloader");
            var chainInst = chainType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
            var pluginsDict = chainInst?.GetType().GetProperty("Plugins", BindingFlags.Public | BindingFlags.Instance)?.GetValue(chainInst) as IDictionary;
            if (pluginsDict == null) return null;

            foreach (DictionaryEntry de in pluginsDict)
            {
                var pInfo = de.Value;
                var inst = pInfo?.GetType().GetProperty("Instance", BindingFlags.Public | BindingFlags.Instance)?.GetValue(pInfo);
                if (inst == null || inst.GetType().FullName != "Stellar.Host.BootstrapPlugin")
                    continue;

                var reg = inst.GetType().GetField("_pluginRegistry", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(inst);
                var slots = reg?.GetType().GetField("_slots", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(reg) as IDictionary;
                if (slots == null) continue;

                foreach (DictionaryEntry slotDe in slots)
                {
                    var slot = slotDe.Value;
                    var plugObj = slot?.GetType().GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(slot);
                    if (plugObj != null && plugObj.GetType().FullName == targetTypeFullName)
                        return plugObj;
                }
            }
        }
        catch { }
        return null;
    }

    // ─────────────────────── Stellar.AutoGather ───────────────────────

    public bool EnsureAutoGatherResolved()
    {
        if (_gatherPluginInstance != null && _gatherControllerInstance != null)
            return true;

        try
        {
            object? foundPlugin = TryFindPluginInstance(AutoGatherPluginTypeName);
            if (foundPlugin == null)
                return false;

            var pType = foundPlugin.GetType();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var fiCtrl = pType.GetField("_autoGatherController", flags);
            var ctrl = fiCtrl?.GetValue(foundPlugin);
            if (ctrl == null)
                return false;

            var cType = ctrl.GetType();
            _gatherPluginInstance = foundPlugin;
            _gatherControllerInstance = ctrl;

            _miSetAutoGatherEnabled = pType.GetMethod("SetAutoGatherEnabled", flags);
            _miSetAutoGatherFocus = pType.GetMethod("SetAutoGatherFocus", flags);

            _fiGatherEnabled = cType.GetField("_enabled", flags);
            _fiLockedConfigId = cType.GetField("_lockedConfigId", flags);
            _fiLockedName = cType.GetField("_lockedName", flags);
            _piGatherStatus = cType.GetProperty("Status", flags);
            _piGatherFocusMode = cType.GetProperty("FocusMode", flags);

            _services.Log.Info("[AgyBridge][Gather] Connected to Stellar.AutoGather");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] Failed to resolve Stellar.AutoGather: {ex.Message}");
            return false;
        }
    }

    public bool IsAutoGatherEnabled()
    {
        if (!EnsureAutoGatherResolved()) return false;
        try
        {
            return _fiGatherEnabled?.GetValue(_gatherControllerInstance) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    public string GetAutoGatherStatus()
    {
        if (!EnsureAutoGatherResolved()) return "unavailable";
        try
        {
            return _piGatherStatus?.GetValue(_gatherControllerInstance)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Harmony postfix: AutoGatherController.OnGatherComplete(string via).</summary>
    private static void OnGatherCompletePostfix(string via)
    {
        System.Threading.Interlocked.Increment(ref _completedGathers);
        _lastGatherCompletionVia = via ?? "";
    }

    public string LastGatherCompletionVia => _lastGatherCompletionVia;

    private void EnsureGatherCompletionHook()
    {
        if (_completionHooked || _completionHookTried || _gatherControllerInstance == null) return;
        _completionHookTried = true;
        try
        {
            var mi = _gatherControllerInstance.GetType().GetMethod(
                "OnGatherComplete",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            if (mi == null)
            {
                _services.Log.Warning("[AgyBridge][Gather] AutoGatherController.OnGatherComplete(string) not found — gather counting unavailable");
                return;
            }

            _harmony ??= _services.Harmony.Create("agybridge.autogather");
            _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(ExternalPluginAdapter), nameof(OnGatherCompletePostfix)));
            _completionHooked = true;
            _services.Log.Info("[AgyBridge][Gather] Hooked AutoGather.OnGatherComplete for exact completion counting");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] Hooking OnGatherComplete failed: {ex.Message}");
        }
    }

    public long ReadGatherCompletedCount()
    {
        if (!EnsureAutoGatherResolved()) return -1L;
        EnsureGatherCompletionHook();
        return _completionHooked ? System.Threading.Interlocked.Read(ref _completedGathers) : -1L;
    }

    public bool StartAutoGather(int configId, string lockedName, bool? useFocus = null)
    {
        if (!EnsureAutoGatherResolved()) return false;
        try
        {
            if (useFocus.HasValue)
            {
                if (_miSetAutoGatherFocus != null)
                    _miSetAutoGatherFocus.Invoke(_gatherPluginInstance, new object[] { useFocus.Value });
                else
                    _piGatherFocusMode?.SetValue(_gatherControllerInstance, useFocus.Value);
            }

            _fiLockedConfigId?.SetValue(_gatherControllerInstance, configId);
            _fiLockedName?.SetValue(_gatherControllerInstance, configId > 0 ? lockedName : "");

            if (_miSetAutoGatherEnabled != null)
            {
                _miSetAutoGatherEnabled.Invoke(_gatherPluginInstance, new object[] { true });
                return true;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] StartAutoGather failed: {ex.Message}");
        }
        return false;
    }

    public void StopAutoGather()
    {
        if (!EnsureAutoGatherResolved()) return;
        try
        {
            _miSetAutoGatherEnabled?.Invoke(_gatherPluginInstance, new object[] { false });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] StopAutoGather failed: {ex.Message}");
        }
    }

    public JsonObject? QueryGatherResourceTarget(string trimmedQuery, int localSceneId, Position3D localPos)
    {
        if (!_lua.IsReady || string.IsNullOrWhiteSpace(trimmedQuery))
            return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQ = {LuaLiteral.Quote(trimmedQuery)}\nlocal curSid = {localSceneId}\nlocal lx = {localPos.X:F2}\nlocal lz = {localPos.Z:F2}\n");

        const string body = @"
local function norm(s)
  s = tostring(s or ''):lower()
  s = s:gsub('[%s%-%_·・%(%)]+', '')
  return s
end

local q = norm(rawQ)
local asNum = tonumber(rawQ)

local lct = Z.TableMgr.GetTable('LifeCollectListTableMgr')
local ctm = Z.TableMgr.GetTable('CollectionTableMgr')
local cet = Z.TableMgr.GetTable('CollectionEnrichmentTableMgr')
local stm = Z.TableMgr.GetTable('SceneTableMgr')

local matchedId = 0
local matchedName = ''
local fallbackScene = 0

-- 1. Check LifeCollectListTableMgr first (81 life-skill gatherables with exact Scene mapping)
if lct and lct.GetDatas then
  local exact, partial = nil, nil
  for id, r in pairs(lct.GetDatas()) do
    if not r.Hide then
      local cid = tonumber(r.Id or id) or 0
      local nm = tostring(r.Name or '')
      local nn = norm(nm)
      if asNum and cid == asNum then
        exact = r break
      elseif nn == q and nn ~= '' then
        exact = r break
      elseif nn ~= '' and (nn:find(q, 1, true) or (#nn >= 2 and q:find(nn, 1, true))) then
        if not partial then partial = r end
      end
    end
  end
  local pick = exact or partial
  if pick then
    matchedId = tonumber(pick.Id) or 0
    matchedName = tostring(pick.Name or '')
    fallbackScene = tonumber(pick.Scene or 0) or 0
  end
end

-- 2. Fallback to CollectionTableMgr if not in LifeCollectListTableMgr
if matchedId <= 0 and ctm and ctm.GetDatas then
  local exact, partial = nil, nil
  for id, r in pairs(ctm.GetDatas()) do
    local cid = tonumber(r.Id or id) or 0
    local nm = tostring(r.CollectionName or '')
    local nn = norm(nm)
    if asNum and cid == asNum then
      exact = r break
    elseif nn == q and nn ~= '' then
      exact = r break
    elseif nn ~= '' and cid >= 20000000 and nn:find(q, 1, true) then
      if not partial then partial = r end
    end
  end
  local pick = exact or partial
  if pick then
    matchedId = tonumber(pick.Id) or 0
    matchedName = tostring(pick.CollectionName or '')
  end
end

if matchedId <= 0 then
  return { found = false }
end

-- 3. Find best cluster in CollectionEnrichmentTableMgr
local bestSameScene = nil
local bestSameDistSq = nil
local bestOtherScene = nil
local bestOtherCount = -1

if cet and cet.GetDatas then
  for key, r in pairs(cet.GetDatas()) do
    if tonumber(r.CollectionId or 0) == matchedId and r.CenterPosition then
      local keyNum = tonumber(r.Id or key) or 0
      local sid = math.floor(keyNum / 10000000000)
      local cnt = tonumber(r.Count or 1) or 1
      local cx = tonumber(r.CenterPosition[1] or r.CenterPosition.x or 0) or 0
      local cy = tonumber(r.CenterPosition[2] or r.CenterPosition.y or 0) or 0
      local cz = tonumber(r.CenterPosition[3] or r.CenterPosition.z or 0) or 0

      if sid == curSid and curSid > 0 then
        local dx = cx - lx
        local dz = cz - lz
        local dsq = dx * dx + dz * dz
        if bestSameDistSq == nil or dsq < bestSameDistSq then
          bestSameDistSq = dsq
          bestSameScene = { sid = sid, x = cx, y = cy, z = cz, count = cnt }
        end
      elseif sid > 0 then
        local srow = stm and stm.GetRow(sid, true)
        local sub = srow and tonumber(srow.SceneSubType or 0) or 0
        if (sub == 4 or sub == 3 or sid == fallbackScene) and cnt > bestOtherCount then
          bestOtherCount = cnt
          bestOtherScene = { sid = sid, x = cx, y = cy, z = cz, count = cnt }
        end
      end
    end
  end
end

local chosen = bestSameScene or bestOtherScene
local finalSid = chosen and chosen.sid or (fallbackScene > 0 and fallbackScene or curSid)
local sName = ''
if stm and finalSid > 0 then
  local sr = stm.GetRow(finalSid, true)
  if sr and sr.Name then sName = tostring(sr.Name) end
end

return {
  found = true,
  id = matchedId,
  name = matchedName,
  sceneId = finalSid,
  sceneName = sName,
  hasCluster = (chosen ~= nil),
  x = chosen and chosen.x or 0,
  y = chosen and chosen.y or 0,
  z = chosen and chosen.z or 0,
  count = chosen and chosen.count or 0
}
";
        var res = _lua.Eval(header + body);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonObject obj || obj["found"]?.GetValue<bool>() != true)
            return null;

        return JsonNode.Parse(obj.ToJsonString()) as JsonObject ?? obj;
    }

    public JsonObject QueryGatherableResources(string filter)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string q = LuaLiteral.Quote(filter.Trim());
        var res = _lua.Eval($@"
local q = tostring({q}):lower()
local lct = Z.TableMgr.GetTable('LifeCollectListTableMgr')
local list = {{}}
if lct and lct.GetDatas then
  for id, r in pairs(lct.GetDatas()) do
    if not r.Hide then
      local nm = tostring(r.Name or '')
      local area = tostring(r.ProduceArea or '')
      if q == '' or nm:lower():find(q, 1, true) or area:lower():find(q, 1, true) then
        list[#list + 1] = {{
          id = tonumber(r.Id or id) or 0,
          name = nm,
          scene_id = tonumber(r.Scene or 0) or 0,
          area = area,
          need_level = tonumber(r.NeedLevel or 0) or 0,
          focus_cost = tonumber(r.Cost or 0) or 0
        }}
      end
    end
  end
end
table.sort(list, function(a, b) return a.id < b.id end)
return {{ items = list }}
");
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject inner)
        {
            var outObj = JsonNode.Parse(inner.ToJsonString()) as JsonObject ?? new JsonObject();
            outObj["ok"] = true;
            return outObj;
        }
        return res;
    }

    // ─────────────────────── Stellar.AutoFishing ───────────────────────

    public bool EnsureAutoFishingResolved()
    {
        if (_fishPluginInstance != null)
            return true;

        try
        {
            object? found = TryFindPluginInstance(AutoFishingPluginTypeName);
            if (found == null)
                return false;

            var t = found.GetType();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var startAuto = t.GetMethod("StartAuto", flags);
            var stopAuto = t.GetMethod("StopAuto", flags);
            if (startAuto == null || stopAuto == null)
                return false;

            _fishPluginInstance = found;
            _miStartAuto = startAuto;
            _miStopAuto = stopAuto;
            _piFishState = t.GetProperty("State", flags);
            _fiAutoEnabled = t.GetField("_autoEnabled", flags);
            _fiFishCaught = t.GetField("_fishCaught", flags);
            _fiFishMissed = t.GetField("_fishMissed", flags);
            _fiRodChanged = t.GetField("_rodChanged", flags);

            _services.Log.Info("[AgyBridge][Fishing] Connected to Stellar.AutoFishing");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Fishing] Failed to resolve Stellar.AutoFishing: {ex.Message}");
            return false;
        }
    }

    public bool IsAutoFishingEnabled()
    {
        if (!EnsureAutoFishingResolved()) return false;
        try
        {
            return _fiAutoEnabled?.GetValue(_fishPluginInstance) is bool b && b;
        }
        catch { return false; }
    }

    public int GetAutoFishingStateCode()
    {
        if (!EnsureAutoFishingResolved() || _piFishState == null) return -1;
        try
        {
            var v = _piFishState.GetValue(_fishPluginInstance);
            return v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : -1;
        }
        catch { return -1; }
    }

    public int ReadFishCaughtCount() => ReadFishFieldInt(_fiFishCaught);
    public int ReadFishMissedCount() => ReadFishFieldInt(_fiFishMissed);
    public int ReadFishRodsUsedCount() => ReadFishFieldInt(_fiRodChanged);

    private int ReadFishFieldInt(FieldInfo? field)
    {
        if (!EnsureAutoFishingResolved() || field == null) return 0;
        try { return Convert.ToInt32(field.GetValue(_fishPluginInstance) ?? 0, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    public bool StartAutoFishing()
    {
        if (!EnsureAutoFishingResolved() || _miStartAuto == null) return false;
        try
        {
            _miStartAuto.Invoke(_fishPluginInstance, null);
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Fishing] StartAuto failed: {ex.Message}");
            return false;
        }
    }

    public void StopAutoFishing()
    {
        if (!EnsureAutoFishingResolved() || _miStopAuto == null) return;
        try
        {
            _miStopAuto.Invoke(_fishPluginInstance, null);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Fishing] StopAuto failed: {ex.Message}");
        }
    }

    public void ExitFishingStateCoroutine()
    {
        if (!_lua.IsReady) return;
        _lua.StartCoroutineRpc(@"
pcall(function()
  if Z.UIMgr and Z.UIMgr:IsActive('fishing_obtain_window') then
    Z.UIMgr:CloseView('fishing_obtain_window')
  end
end)
pcall(function()
  local fvm = Z.VMMgr and Z.VMMgr.GetVM('fishing')
  if fvm then
    local fd = Z.DataMgr and Z.DataMgr.Get('fishing_data')
    local st = fd and fd.FishingStage or 0
    local inFishUi = Z.UIMgr and (Z.UIMgr:IsActive('fishing_main_window') or Z.UIMgr:IsActive('fishing_obtain_window'))
    if inFishUi or (st ~= 0 and st ~= 12 and st ~= 15) then
      local cs = Z.CancelSource.Rent()
      if fvm.AsyncQuitFishingState then
        pcall(function() fvm.AsyncQuitFishingState(cs:CreateToken()) end)
      end
      cs:Recycle()
      if fvm.QuitFishingUI then pcall(function() fvm.QuitFishingUI() end) end
    end
  end
end)
pcall(function()
  if Z.InteractionMgr and Z.InteractionMgr.AbortInteractionByUI then
    Z.InteractionMgr:AbortInteractionByUI()
  end
end)
return { ok = true }
");
    }

    public JsonObject EvaluateRodAndBait(IReadOnlyList<int>? preferredBaitIds)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua 런타임이 아직 준비되지 않았어요." };

        string baitArrLua = "{}";
        if (preferredBaitIds != null && preferredBaitIds.Count > 0)
            baitArrLua = "{ " + string.Join(", ", preferredBaitIds.Select(id => id.ToString(CultureInfo.InvariantCulture))) + " }";

        string code = $@"
local prefBaits = {baitArrLua}
local prefSet = {{}}
for _, bid in ipairs(prefBaits) do prefSet[bid] = true end

local fd = Z.DataMgr.Get('fishing_data')
local itm = Z.TableMgr.GetTable('ItemTableMgr')
local pkgs = Z.ContainerMgr and Z.ContainerMgr.CharSerialize and Z.ContainerMgr.CharSerialize.itemPackage and Z.ContainerMgr.CharSerialize.itemPackage.packages
if not pkgs or not itm then
  return {{ ok = false, error = '인벤토리 데이터를 읽을 수 없어요.' }}
end

local function getRodDur(uid)
  if not (fd and fd.GetFishingRodDurability and uid and uid > 0) then
    return 1
  end
  local ok, res = pcall(function() return fd:GetFishingRodDurability(uid) end)
  if ok and res ~= nil then
    local n = tonumber(res)
    if n ~= nil then return n end
  end
  return 1
end

local curRodUuid = fd and tonumber(fd.FishingRod or 0) or 0
local curRodDur = 0
local curBaitCfg = fd and tonumber(fd.FishBait or 0) or 0

local bestRodUuid, bestRodQuality = 0, -1
local curRodOwned = false
local curBaitCount = 0
local bestPrefBaitCfg, bestPrefQuality = 0, -1
local bestAnyBaitCfg, bestAnyQuality = 0, -1

for _, pkg in pairs(pkgs) do
  if pkg and pkg.items then
    for _, item in pairs(pkg.items) do
      local cid = tonumber(item.configId or 0) or 0
      local cnt = tonumber(item.count or 0) or 0
      if cid > 0 and cnt > 0 then
        local cfg = itm.GetRow(cid, true)
        local itype = cfg and tonumber(cfg.Type or 0) or 0
        local q = cfg and tonumber(cfg.Quality or 0) or 0
        local uidNum = tonumber(item.uuid or 0) or 0
        if itype == 1103 then
          local dur = getRodDur(uidNum)
          if curRodUuid > 0 and uidNum == curRodUuid then
            curRodOwned = true
            curRodDur = dur
          end
          if dur > 0 and q > bestRodQuality then
            bestRodQuality = q
            bestRodUuid = uidNum
          end
        elseif itype == 1102 then
          if cid == curBaitCfg then curBaitCount = curBaitCount + cnt end
          if prefSet[cid] and q > bestPrefQuality then
            bestPrefQuality = q
            bestPrefBaitCfg = cid
          end
          if q > bestAnyQuality then
            bestAnyQuality = q
            bestAnyBaitCfg = cid
          end
        end
      end
    end
  end
end

if bestRodUuid <= 0 and not (curRodOwned and curRodDur > 0) then
  return {{ ok = false, reason = 'no_rod', error = '가방에 사용 가능한 낚싯대(내구도 남음)가 없어서 낚시를 할 수 없어요.' }}
end

local needEquipRodUuid = 0
if (not curRodOwned) or curRodUuid <= 0 or curRodDur <= 0 then
  needEquipRodUuid = bestRodUuid
end

local hasPrefConstraint = (#prefBaits > 0)
local chosenBaitCfg = 0
if hasPrefConstraint then
  if curBaitCfg > 0 and curBaitCount > 0 and prefSet[curBaitCfg] then
    chosenBaitCfg = curBaitCfg
  elseif bestPrefBaitCfg > 0 then
    chosenBaitCfg = bestPrefBaitCfg
  elseif bestAnyBaitCfg > 0 then
    chosenBaitCfg = bestAnyBaitCfg
  elseif curBaitCount > 0 then
    chosenBaitCfg = curBaitCfg
  end
else
  if curBaitCfg > 0 and curBaitCount > 0 then
    chosenBaitCfg = curBaitCfg
  elseif bestAnyBaitCfg > 0 then
    chosenBaitCfg = bestAnyBaitCfg
  end
end

if chosenBaitCfg <= 0 then
  return {{ ok = false, reason = 'no_bait', error = '가방에 사용할 수 있는 낚시 미끼가 없어서 낚시를 할 수 없어요.' }}
end

local needEquipBaitCfg = (chosenBaitCfg ~= curBaitCfg) and chosenBaitCfg or 0
return {{
  ok = true,
  need_rod_uuid = tostring(needEquipRodUuid),
  need_bait_cfg = needEquipBaitCfg,
  chosen_bait_cfg = chosenBaitCfg
}}
";
        return _lua.Eval(code);
    }

    public void EquipRodAndBaitCoroutine(long rodUuid, int baitConfigId)
    {
        if (!_lua.IsReady || (rodUuid <= 0 && baitConfigId <= 0))
            return;

        _lua.StartCoroutineRpc(string.Create(CultureInfo.InvariantCulture, $@"
local fvm = Z.VMMgr and Z.VMMgr.GetVM('fishing')
if not fvm then return {{ ok = false }} end
local rodUuid = {rodUuid}
local baitCfg = {baitConfigId}
if rodUuid > 0 and fvm.SetFishingRodAsync then
  local cs = Z.CancelSource.Rent()
  pcall(function() fvm.SetFishingRodAsync(rodUuid, cs:CreateToken()) end)
  cs:Recycle()
end
if baitCfg > 0 and fvm.SetFishingBaitAsync then
  local cs2 = Z.CancelSource.Rent()
  pcall(function() fvm.SetFishingBaitAsync(baitCfg, cs2:CreateToken()) end)
  cs2:Recycle()
end
return {{ ok = true, rod = rodUuid, bait = baitCfg }}
"));
    }

    public bool IsInFishingUiLua()
    {
        if (!_lua.IsReady) return false;
        var res = _lua.Eval(@"
local fd = Z.DataMgr and Z.DataMgr.Get('fishing_data')
local inUi = Z.UIMgr and Z.UIMgr:IsActive('fishing_main_window')
if inUi then return true end
return false
");
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public bool HasFishingInteractionInRange()
    {
        if (!_lua.IsReady) return false;
        var res = _lua.Eval(HasFishingInteractionLua);
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public bool TryTriggerFishingInteraction()
    {
        if (!_lua.IsReady) return false;
        var res = _lua.Eval(TriggerFishingInteractionLua);
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public JsonObject QueryFishingTarget(string trimmedQuery, int localSceneId, Position3D localPos)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQ = {LuaLiteral.Quote(trimmedQuery)}\nlocal curSid = {localSceneId}\nlocal lx = {localPos.X:F2}\nlocal lz = {localPos.Z:F2}\n");

        return _lua.Eval(header + SharedCatalogHelperLua + ResolveFishingTargetBodyLua);
    }

    public JsonObject QueryFishingSpots(string trimmedFilter, int localSceneId, Position3D localPos)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQ = {LuaLiteral.Quote(trimmedFilter)}\nlocal curSid = {localSceneId}\nlocal lx = {localPos.X:F2}\nlocal lz = {localPos.Z:F2}\n");

        var res = _lua.Eval(header + SharedCatalogHelperLua + ListFishingSpotsBodyLua);
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj)
        {
            var outObj = JsonNode.Parse(obj.ToJsonString()) as JsonObject ?? new JsonObject();
            outObj["ok"] = true;
            return outObj;
        }
        return res;
    }

    private const string SharedFishingMatcherLua = @"
local itbl = Z.TableMgr and Z.TableMgr.GetTable('InteractiveTableMgr')
local iov = Z.TableMgr and Z.TableMgr.GetTable('InteractionOverrideTableMgr')
local function isFishingOption(e)
  if not e then return false end
  local cfgId = tonumber(e.GetInteractionCfgId and e:GetInteractionCfgId() or 0) or 0
  local tpl = tonumber(e.GetTemplateId and e:GetTemplateId() or 0) or 0
  local btn = tonumber(e.GetReplaceBtnId and e:GetReplaceBtnId() or 0) or 0
  if cfgId == 2200 or tpl == 2200 or btn == 5006 then return true end
  if iov and cfgId > 0 then
    local ov = iov.GetRow(cfgId, true)
    if ov and tonumber(ov.Id or 0) == 2200 then return true end
  end
  if itbl and cfgId > 0 then
    local ir = itbl.GetRow(cfgId, true)
    if ir and tonumber(ir.TriggerBtnId or 0) == 5006 then return true end
  end
  local txt = ''
  pcall(function() txt = tostring(e:GetContentStr() or '') end)
  local txtLow = txt:lower()
  if txt:find('낚시', 1, true) or txtLow:find('fish', 1, true) or txt:find('釣り', 1, true) then return true end
  return false
end
";

    private const string HasFishingInteractionLua = SharedFishingMatcherLua + @"
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) ~= 'table' then return false end
for _, e in pairs(list) do
  if isFishingOption(e) then return true end
end
return false
";

    private const string TriggerFishingInteractionLua = SharedFishingMatcherLua + @"
if not Z.InteractionMgr then return false end
local idata = Z.DataMgr and Z.DataMgr.Get('interaction_data')
local list = idata and idata.GetData and idata:GetData()
if type(list) ~= 'table' then return false end
for _, e in pairs(list) do
  if isFishingOption(e) then
    local uuid = e:GetUuid()
    local cfgId = e:GetInteractionCfgId()
    local tpl = e:GetTemplateId()
    local seat = e:GetSeatGroupIndex()
    local cond = true
    pcall(function() cond = Z.InteractionMgr:CheckCondition(uuid, cfgId, seat, false) end)
    if cond ~= false then
      Z.InteractionMgr:BeginInteraction(uuid, cfgId, tpl, seat)
      return true
    end
  end
end
return false
";

    private const string SharedCatalogHelperLua = @"
local function norm(s)
  s = tostring(s or ''):lower()
  s = s:gsub('아스테리즈', '아스텔리즈')
  s = s:gsub('순간%s*이동%s*[-–:]?%s*', '')
  s = s:gsub('teleport%s*[-–:]?%s*', '')
  s = s:gsub('warp%s*[-–:]?%s*', '')
  s = s:gsub('テレポート%s*[-–：:]?%s*', '')
  s = s:gsub('転送%s*[-–：:]?%s*', '')
  s = s:gsub('[%s%p·・]+', '')
  return s
end

local function tokenize(raw)
  local cleaned = tostring(raw or ''):lower()
  cleaned = cleaned:gsub('아스테리즈', '아스텔리즈')
  for _, sw in ipairs({
    '낚시터', '낚시 포인트', '낚시포인트', '낚시', '포인트', '할 수 있는 곳',
    '근처', '주변', '여기', '이곳', '이 근처', '가까운', '아무데나', '보유',
    'fishing spot', 'fishing point', 'fishing', 'fish', 'spot', 'nearby', 'near', 'here', 'anywhere', 'around',
    '釣り場', '釣りポイント', '釣り', '近く', '周辺', 'ここ', 'どこでも'
  }) do
    cleaned = cleaned:gsub(sw, ' ')
  end
  local out = {}
  for tok in cleaned:gmatch('%S+') do
    tok = tok:gsub('(에서|으로|로|에|가서|와|을|를|이나|나)$', '')
    tok = norm(tok)
    if #tok >= 2 then out[#out + 1] = tok end
  end
  return out
end

local function buildFishingCatalog()
  local fat = Z.TableMgr.GetTable('FishingAreaTableMgr')
  local ft = Z.TableMgr.GetTable('FishingTableMgr')
  local itm = Z.TableMgr.GetTable('ItemTableMgr')
  local sot = Z.TableMgr.GetTable('SceneObjectTableMgr')
  local soeg = Z.TableMgr.GetTable('SceneObjectEntityGlobalTableMgr')
  local stm = Z.TableMgr.GetTable('SceneTableMgr')
  local ttm = Z.TableMgr.GetTable('TransferTableMgr')
  local mapVm = Z.VMMgr.GetVM('map')

  local areaById = {}
  local sobjToArea = {}
  if fat and fat.GetDatas then
    for id, ar in pairs(fat.GetDatas()) do
      local aid = tonumber(ar.Id or id) or 0
      local sobjId = tonumber(ar.SceneObjectId or 0) or 0
      local aname = tostring(ar.AreaName or '')
      areaById[aid] = { area_id = aid, area_name = aname, sobj_id = sobjId }
      if sobjId > 0 then sobjToArea[sobjId] = aid end
    end
  end

  local fishRows = {}
  local areaPrimaryScene = {}
  if ft and ft.GetDatas then
    for id, fr in pairs(ft.GetDatas()) do
      local fid = tonumber(fr.FishId or id) or 0
      local irow = itm and itm.GetRow(fid, true) or nil
      local fname = irow and tostring(irow.Name or '') or tostring(fid)
      local aid = tonumber(fr.FishingAreaId or 0) or 0
      local baits = {}
      if type(fr.BaitId) == 'table' then
        for _, bid in ipairs(fr.BaitId) do
          if tonumber(bid or 0) > 0 then baits[#baits + 1] = tonumber(bid) end
        end
      end
      fishRows[#fishRows + 1] = {
        fish_id = fid,
        name = fname,
        name_norm = norm(fname),
        area_id = aid,
        baits = baits,
        quick_jump = fr.QuickJumpParam
      }
      if type(fr.QuickJumpParam) == 'table' and #fr.QuickJumpParam >= 3 and aid > 0 and soeg then
        local qSid = tonumber(fr.QuickJumpParam[1] or 0) or 0
        local qUid = tonumber(fr.QuickJumpParam[3] or 0) or 0
        if qSid > 0 and not areaPrimaryScene[aid] then areaPrimaryScene[aid] = qSid end
        local gKey = qSid * 10000000 + qUid
        local gRow = soeg.GetRow(gKey, true)
        if gRow and tonumber(gRow.Id or 0) > 0 then
          local sobjId = tonumber(gRow.Id)
          if not sobjToArea[sobjId] then sobjToArea[sobjId] = aid end
        end
      end
    end
  end

  -- Discover ALL SceneObjectIds representing a Fishing Point (InteractionTemplate contains 2200 or localized Fishing Spot name)
  local fishSobjSet = {}
  if sot and sot.GetDatas then
    for id, r in pairs(sot.GetDatas()) do
      local rn = tostring(r.Name or ''):lower()
      local isFish = (rn == '낚시 포인트' or rn == 'fishing spot' or rn == 'fishing point' or rn == '釣りポイント' or rn == '釣り場')
      if not isFish and type(r.InteractionTemplate) == 'table' then
        for _, tpl in ipairs(r.InteractionTemplate) do
          if type(tpl) == 'table' and tonumber(tpl[2] or 0) == 2200 then
            isFish = true break
          end
        end
      end
      if isFish then fishSobjSet[tonumber(id)] = true end
    end
  end
  for sobjId, _ in pairs(sobjToArea) do fishSobjSet[sobjId] = true end

  -- Cache unlocked/available warp landmarks per scene
  local warpsByScene = {}
  local function getSceneWarps(sid)
    if warpsByScene[sid] ~= nil then return warpsByScene[sid] end
    local wlist = {}
    if mapVm and mapVm.LoadEntityTableData then
      local okE, ents = pcall(function() return mapVm.LoadEntityTableData(sid, 0) end)
      if okE and type(ents) == 'table' then
        for _, e in pairs(ents) do
          local tpId = tonumber(e.TpPointId or 0) or 0
          if tpId > 0 and e.Pos then
            local unlocked = true
            if mapVm.CheckTransferPointUnlock then
              local okU, u = pcall(function() return mapVm.CheckTransferPointUnlock(tpId) end)
              if okU then unlocked = (u == true) end
            end
            local tr = ttm and ttm.GetRow(tpId, true) or nil
            local nm = tr and tostring(tr.Name or '') or ''
            nm = nm:gsub('^순간%s*이동%s*[-–:]?%s*', '')
            nm = nm:gsub('^[Tt]eleport%s*[-–:]?%s*', '')
            nm = nm:gsub('^[Ww]arp%s*[-–:]?%s*', '')
            nm = nm:gsub('^テレポート%s*[-–：:]?%s*', '')
            nm = nm:gsub('^転送%s*[-–：:]?%s*', '')
            wlist[#wlist + 1] = {
              tp_id = tpId,
              name = nm,
              unlocked = unlocked,
              x = tonumber(e.Pos.x or 0) or 0,
              z = tonumber(e.Pos.z or 0) or 0
            }
          end
        end
      end
    end
    warpsByScene[sid] = wlist
    return wlist
  end

  -- Collect raw spots from SceneObjectEntityGlobalTableMgr
  local rawSpots = {}
  local sceneHasDirectArea = {}
  if soeg and soeg.GetDatas then
    for key, r in pairs(soeg.GetDatas()) do
      local sobjId = tonumber(r.Id or 0) or 0
      if fishSobjSet[sobjId] and r.Position then
        local knum = tonumber(key) or 0
        local sid = math.floor(knum / 10000000)
        local uid = knum % 10000000
        local srow = stm and stm.GetRow(sid, true) or nil
        local st = srow and tonumber(srow.SceneSubType or 0) or 0
        if st == 3 or st == 4 then
          local px = tonumber(r.Position[1] or r.Position.x or 0) or 0
          local py = tonumber(r.Position[2] or r.Position.y or 0) or 0
          local pz = tonumber(r.Position[3] or r.Position.z or 0) or 0
          local aid = sobjToArea[sobjId] or 0
          if aid > 0 then
            if not sceneHasDirectArea[sid] then sceneHasDirectArea[sid] = {} end
            table.insert(sceneHasDirectArea[sid], { area_id = aid, x = px, z = pz })
          end

          local bestW, bestWd = '', 999999
          for _, w in ipairs(getSceneWarps(sid)) do
            local d = math.sqrt((px - w.x)^2 + (pz - w.z)^2)
            if d < bestWd then bestWd = d; bestW = w.name end
          end

          rawSpots[#rawSpots + 1] = {
            scene_id = sid,
            scene_name = srow and tostring(srow.Name or '') or tostring(sid),
            area_id = aid,
            area_name = '',
            landmark = bestW,
            sobj_id = sobjId,
            uid = uid,
            x = px,
            y = py,
            z = pz,
            dist = (sid == curSid and curSid > 0) and math.floor(math.sqrt((px - lx)^2 + (pz - lz)^2) * 10) / 10 or 99999
          }
        end
      end
    end
  end

  -- Fill missing area_id for secondary SceneObject variants (e.g. 2206..2210, 2212) using nearest known area anchor in same scene
  for _, sp in ipairs(rawSpots) do
    if sp.area_id <= 0 then
      local anchors = sceneHasDirectArea[sp.scene_id]
      if anchors and #anchors > 0 then
        local bestAid, bestD = anchors[1].area_id, 999999
        for _, a in ipairs(anchors) do
          local d = (sp.x - a.x)^2 + (sp.z - a.z)^2
          if d < bestD then bestD = d; bestAid = a.area_id end
        end
        sp.area_id = bestAid
      end
    end
    local ainfo = areaById[sp.area_id]
    sp.area_name = ainfo and ainfo.area_name or ''
  end

  return { spots = rawSpots, fish = fishRows, areas = areaById, areaPrimaryScene = areaPrimaryScene }
end
";

    private const string ResolveFishingTargetBodyLua = @"
local cat = buildFishingCatalog()
if #cat.spots == 0 then
  return { found = false, error = '낚시 포인트 데이터를 찾지 못했어요.' }
end

local qNorm = norm(rawQ)
local hereWords = {
  ['']=true, ['여기']=true, ['근처']=true, ['주변']=true, ['이곳']=true, ['이근처']=true, ['가까운곳']=true, ['아무데나']=true, ['낚시']=true, ['낚시터']=true,
  ['here']=true, ['nearby']=true, ['near']=true, ['around']=true, ['anywhere']=true, ['any']=true, ['fish']=true, ['fishing']=true, ['fishingspot']=true,
  ['ここ']=true, ['近く']=true, ['周辺']=true, ['どこでも']=true, ['釣り']=true, ['釣り場']=true
}

-- 1. Try matching an exact/partial fish species name from FishingTableMgr first
local matchedFish = nil
if not hereWords[qNorm] then
  local exactF, partialF = nil, nil
  for _, fr in ipairs(cat.fish) do
    if fr.name_norm == qNorm and qNorm ~= '' then
      exactF = fr break
    elseif qNorm ~= '' and #qNorm >= 2 and (fr.name_norm:find(qNorm, 1, true) or (#fr.name_norm >= 2 and qNorm:find(fr.name_norm, 1, true))) then
      if not partialF or #fr.name_norm > #partialF.name_norm then partialF = fr end
    end
  end
  matchedFish = exactF or partialF
end

local ranked = {}
if matchedFish then
  local primarySid = cat.areaPrimaryScene and cat.areaPrimaryScene[matchedFish.area_id] or 0
  for _, sp in ipairs(cat.spots) do
    if sp.area_id == matchedFish.area_id then
      local sc = ((primarySid > 0 and sp.scene_id == primarySid) and 2000 or 0)
        + (sp.scene_id == curSid and 1000 or 0)
        - math.min(900, sp.dist * 0.1)
      ranked[#ranked + 1] = { score = sc, spot = sp }
    end
  end
  table.sort(ranked, function(a, b) return a.score > b.score end)
  local top = {}
  for i = 1, math.min(10, #ranked) do top[i] = ranked[i].spot end
  if #top == 0 then
    return { found = false, error = '[' .. tostring(matchedFish.name) .. '] 서식지 포인트를 찾지 못했어요.' }
  end
  return {
    found = true,
    reason = 'fish_species',
    fish_name = matchedFish.name,
    baits = matchedFish.baits,
    spots = top
  }
end

-- 2. Nearby / current scene request
if hereWords[qNorm] then
  for _, sp in ipairs(cat.spots) do
    local sc = (sp.scene_id == curSid and 10000 or (sp.scene_id == 8 and 500 or 0)) - sp.dist
    ranked[#ranked + 1] = { score = sc, spot = sp }
  end
  table.sort(ranked, function(a, b) return a.score > b.score end)
  local top = {}
  for i = 1, math.min(10, #ranked) do top[i] = ranked[i].spot end
  return { found = true, reason = 'nearest', spots = top }
end

-- 3. Region / Landmark / Water-type token matching across scene_name, area_name, landmark
local toks = tokenize(rawQ)
if #toks == 0 then toks = { qNorm } end

local synonyms = {
  ['바다'] = { '해수', '해변', '바다', '항구' },
  ['바닷가'] = { '해수', '해변', '바다', '항구' },
  ['해변'] = { '해변', '해수', '바다' },
  ['민물'] = { '담수', '호수', '계류', '계곡' },
  ['강'] = { '담수', '계류', '계곡' },
  ['강가'] = { '담수', '계류', '계곡' },
  ['호수'] = { '호수', '담수' },
  ['sea'] = { 'sea', 'ocean', 'beach', 'coast', 'harbor', 'port', 'saltwater', 'bay', 'shore' },
  ['ocean'] = { 'ocean', 'sea', 'beach', 'coast', 'harbor', 'port', 'saltwater', 'bay' },
  ['beach'] = { 'beach', 'coast', 'sea', 'ocean', 'bay', 'shore' },
  ['coast'] = { 'coast', 'beach', 'sea', 'ocean', 'harbor', 'port' },
  ['harbor'] = { 'harbor', 'port', 'sea', 'coast' },
  ['port'] = { 'port', 'harbor', 'sea', 'coast' },
  ['river'] = { 'river', 'stream', 'lake', 'freshwater', 'pond', 'creek', 'brook', 'valley' },
  ['stream'] = { 'stream', 'river', 'freshwater', 'creek', 'brook', 'valley' },
  ['lake'] = { 'lake', 'freshwater', 'pond', 'river' },
  ['freshwater'] = { 'freshwater', 'lake', 'river', 'stream', 'pond' },
  ['pond'] = { 'pond', 'lake', 'freshwater' },
  ['海'] = { '海', '海水', '海岸', '浜', '港', 'ビーチ' },
  ['海岸'] = { '海岸', '海', '浜', 'ビーチ', '港' },
  ['浜'] = { '浜', '海岸', '海', 'ビーチ' },
  ['港'] = { '港', '海', '海岸' },
  ['川'] = { '川', '渓流', '淡水', '湖' },
  ['渓流'] = { '渓流', '川', '淡水', '渓谷' },
  ['湖'] = { '湖', '淡水', '池' },
  ['淡水'] = { '淡水', '湖', '川', '渓流', '池' },
}

for _, sp in ipairs(cat.spots) do
  local hay = norm(sp.scene_name) .. ' ' .. norm(sp.area_name) .. ' ' .. norm(sp.landmark)
  local hitScore = 0
  local allMatched = true
  for _, tok in ipairs(toks) do
    local matchedTok = (hay:find(tok, 1, true) ~= nil)
    if matchedTok then
      hitScore = hitScore + 100
    elseif synonyms[tok] then
      for _, syn in ipairs(synonyms[tok]) do
        if hay:find(syn, 1, true) then
          matchedTok = true
          hitScore = hitScore + 75
          break
        end
      end
    end
    if not matchedTok then allMatched = false end
  end

  if hitScore > 0 then
    if allMatched then hitScore = hitScore + 250 end
    local scNorm = norm(sp.scene_name)
    if scNorm ~= '' and (scNorm == qNorm or scNorm:find(qNorm, 1, true) or qNorm:find(scNorm, 1, true)) then
      hitScore = hitScore + 220
    end
    local lmNorm = norm(sp.landmark)
    if lmNorm ~= '' and (lmNorm:find(qNorm, 1, true) or qNorm:find(lmNorm, 1, true)) then
      hitScore = hitScore + 180
    end
    if sp.scene_id == curSid then hitScore = hitScore + 40 end
    hitScore = hitScore - math.min(35, sp.dist * 0.02)
    ranked[#ranked + 1] = { score = hitScore, spot = sp }
  end
end

if #ranked == 0 then
  return { found = false, error = '[' .. tostring(rawQ) .. '] 와 일치하는 낚시터나 물고기를 찾지 못했어요.' }
end

table.sort(ranked, function(a, b)
  if math.abs(a.score - b.score) > 0.01 then return a.score > b.score end
  return a.spot.dist < b.spot.dist
end)
local top = {}
for i = 1, math.min(10, #ranked) do top[i] = ranked[i].spot end
return { found = true, reason = 'location_or_landmark', spots = top }
";

    private const string ListFishingSpotsBodyLua = @"
local cat = buildFishingCatalog()
local q = norm(rawQ)

-- Aggregate unique (scene_id, area_id, landmark) hubs + representative spot coords + fish list
local fishByArea = {}
for _, fr in ipairs(cat.fish) do
  if fr.area_id > 0 then
    if not fishByArea[fr.area_id] then fishByArea[fr.area_id] = {} end
    table.insert(fishByArea[fr.area_id], fr.name)
  end
end

local groups = {}
local groupOrder = {}
for _, sp in ipairs(cat.spots) do
  local hay = norm(sp.scene_name) .. norm(sp.area_name) .. norm(sp.landmark) .. norm(table.concat(fishByArea[sp.area_id] or {}, ','))
  if q == '' or hay:find(q, 1, true) then
    local gk = tostring(sp.scene_id) .. ':' .. tostring(sp.area_id) .. ':' .. tostring(sp.landmark)
    local cur = groups[gk]
    if not cur then
      cur = {
        scene_id = sp.scene_id,
        scene_name = sp.scene_name,
        area_id = sp.area_id,
        area_name = sp.area_name,
        landmark = sp.landmark,
        spot_count = 1,
        best_dist = sp.dist,
        sample_pos = { x = math.floor(sp.x * 10) / 10, y = math.floor(sp.y * 10) / 10, z = math.floor(sp.z * 10) / 10 },
        fish_sample = fishByArea[sp.area_id] or {}
      }
      groups[gk] = cur
      groupOrder[#groupOrder + 1] = cur
    else
      cur.spot_count = cur.spot_count + 1
      if sp.dist < cur.best_dist then
        cur.best_dist = sp.dist
        cur.sample_pos = { x = math.floor(sp.x * 10) / 10, y = math.floor(sp.y * 10) / 10, z = math.floor(sp.z * 10) / 10 }
      end
    end
  end
end

table.sort(groupOrder, function(a, b)
  local aHere = (a.scene_id == curSid) and 1 or 0
  local bHere = (b.scene_id == curSid) and 1 or 0
  if aHere ~= bHere then return aHere > bHere end
  if a.scene_id ~= b.scene_id then return a.scene_id < b.scene_id end
  return a.best_dist < b.best_dist
end)

return {
  current_scene_id = curSid,
  spots = groupOrder,
  total_raw_points = #cat.spots,
  total_fish_species = #cat.fish
}
";
}
