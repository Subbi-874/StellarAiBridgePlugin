using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Gathering;

public sealed class ResolvedGatherTarget
{
    public int CollectionId { get; init; }
    public string Name { get; init; } = "";
    public int SceneId { get; init; }
    public string SceneName { get; init; } = "";
    public Position3D ClusterCenter { get; init; }
    public int ClusterNodeCount { get; init; }
    public bool HasClusterCoords { get; init; }
}

/// <summary>
/// Bridges Stellar.AgyBridge to the installed Stellar.AutoGather plugin in the same AppDomain,
/// allowing target resource locking, Focus mode toggling, exact server CollectCounter tracking,
/// and lookup of gatherable clusters via LifeCollectListTableMgr + CollectionEnrichmentTableMgr.
/// </summary>
public sealed class AutoGatherIntegration
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;

    private object? _pluginInstance;
    private object? _controllerInstance;

    private MethodInfo? _miSetAutoGatherEnabled;
    private MethodInfo? _miSetAutoGatherFocus;
    private FieldInfo? _fiEnabled;
    private FieldInfo? _fiLockedConfigId;
    private FieldInfo? _fiLockedName;
    private PropertyInfo? _piStatus;
    private PropertyInfo? _piFocusMode;

    public AutoGatherIntegration(IPluginServices services, LuaBridge lua)
    {
        _services = services;
        _lua = lua;
    }

    public bool IsAvailable => EnsureResolved();

    public bool EnsureResolved()
    {
        if (_pluginInstance != null && _controllerInstance != null)
            return true;

        try
        {
            object? foundPlugin = TryFindViaLauncherEntries() ?? TryFindViaChainloaderRegistry();
            if (foundPlugin == null)
                return false;

            var pType = foundPlugin.GetType();
            var fiCtrl = pType.GetField("_autoGatherController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var ctrl = fiCtrl?.GetValue(foundPlugin);
            if (ctrl == null)
                return false;

            var cType = ctrl.GetType();
            _pluginInstance = foundPlugin;
            _controllerInstance = ctrl;

            _miSetAutoGatherEnabled = pType.GetMethod("SetAutoGatherEnabled", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _miSetAutoGatherFocus = pType.GetMethod("SetAutoGatherFocus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            _fiEnabled = cType.GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _fiLockedConfigId = cType.GetField("_lockedConfigId", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _fiLockedName = cType.GetField("_lockedName", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _piStatus = cType.GetProperty("Status", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            _piFocusMode = cType.GetProperty("FocusMode", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            _services.Log.Info($"[AgyBridge][Gather] Connected to Stellar.AutoGather");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] Failed to resolve Stellar.AutoGather: {ex.Message}");
            return false;
        }
    }

    private object? TryFindViaLauncherEntries()
    {
        try
        {
            foreach (var entry in _services.Launcher.Entries)
            {
                if (entry == null) continue;
                var eType = entry.GetType();
                foreach (var prop in eType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (typeof(Delegate).IsAssignableFrom(prop.PropertyType))
                    {
                        var del = prop.GetValue(entry) as Delegate;
                        var target = del?.Target;
                        if (target != null && target.GetType().FullName == "Stellar.AutoGather.Plugin")
                            return target;
                    }
                }
                foreach (var fld in eType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (typeof(Delegate).IsAssignableFrom(fld.FieldType))
                    {
                        var del = fld.GetValue(entry) as Delegate;
                        var target = del?.Target;
                        if (target != null && target.GetType().FullName == "Stellar.AutoGather.Plugin")
                            return target;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static object? TryFindViaChainloaderRegistry()
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
                    if (plugObj != null && plugObj.GetType().FullName == "Stellar.AutoGather.Plugin")
                        return plugObj;
                }
            }
        }
        catch { }
        return null;
    }

    public bool IsGatheringEnabled()
    {
        if (!EnsureResolved()) return false;
        try
        {
            return _fiEnabled?.GetValue(_controllerInstance) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    public string GetControllerStatus()
    {
        if (!EnsureResolved()) return "unavailable";
        try
        {
            return _piStatus?.GetValue(_controllerInstance)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ───────── Exact completion counting ─────────
    // The server attr AttrCollectCounter(500) does NOT move for every resource (observed: stays 0 while AutoGather
    // completes nodes via "progress end"), so it cannot be used to verify "gather N times".
    // Instead we hook AutoGatherController.OnGatherComplete(string via) — called exactly once per finished node —
    // with a Harmony postfix owned by Stellar's IHarmonyHost (auto-unpatched on plugin dispose).

    private static long _completedGathers;
    private static string _lastCompletionVia = "";
    private Harmony? _harmony;
    private bool _completionHooked;
    private bool _completionHookTried;

    /// <summary>Harmony postfix: AutoGatherController.OnGatherComplete(string via).</summary>
    private static void OnGatherCompletePostfix(string via)
    {
        System.Threading.Interlocked.Increment(ref _completedGathers);
        _lastCompletionVia = via ?? "";
    }

    public string LastCompletionVia => _lastCompletionVia;

    private void EnsureCompletionHook()
    {
        if (_completionHooked || _completionHookTried || _controllerInstance == null) return;
        _completionHookTried = true;
        try
        {
            var mi = _controllerInstance.GetType().GetMethod(
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
            _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(AutoGatherIntegration), nameof(OnGatherCompletePostfix)));
            _completionHooked = true;
            _services.Log.Info("[AgyBridge][Gather] Hooked AutoGather.OnGatherComplete for exact completion counting");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] Hooking OnGatherComplete failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Monotonic count of nodes AutoGather has finished since the hook was installed (callers use deltas).
    /// Returns -1 when the hook is unavailable, so callers can fail safe instead of gathering forever.
    /// </summary>
    public long ReadCollectCounter()
    {
        if (!EnsureResolved()) return -1L;
        EnsureCompletionHook();
        return _completionHooked ? System.Threading.Interlocked.Read(ref _completedGathers) : -1L;
    }

    public bool StartAutoGather(int configId, string lockedName, bool? useFocus = null)
    {
        if (!EnsureResolved()) return false;
        try
        {
            if (useFocus.HasValue)
            {
                if (_miSetAutoGatherFocus != null)
                    _miSetAutoGatherFocus.Invoke(_pluginInstance, new object[] { useFocus.Value });
                else
                    _piFocusMode?.SetValue(_controllerInstance, useFocus.Value);
            }

            _fiLockedConfigId?.SetValue(_controllerInstance, configId);
            _fiLockedName?.SetValue(_controllerInstance, configId > 0 ? lockedName : "");

            if (_miSetAutoGatherEnabled != null)
            {
                _miSetAutoGatherEnabled.Invoke(_pluginInstance, new object[] { true });
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
        if (!EnsureResolved()) return;
        try
        {
            _miSetAutoGatherEnabled?.Invoke(_pluginInstance, new object[] { false });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Gather] StopAutoGather failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a Korean/partial resource name or numeric CollectionId against LifeCollectListTableMgr,
    /// CollectionTableMgr, and CollectionEnrichmentTableMgr to find both the ConfigId and best 3D cluster coordinates.
    /// </summary>
    public ResolvedGatherTarget? ResolveResourceTarget(string resourceQuery, int localSceneId, Position3D localPos)
    {
        if (!_lua.IsReady || string.IsNullOrWhiteSpace(resourceQuery))
            return null;

        string qTrim = resourceQuery.Trim();
        if (qTrim.Equals("any", StringComparison.OrdinalIgnoreCase)
            || qTrim.Equals("all", StringComparison.OrdinalIgnoreCase)
            || qTrim.Equals("주변", StringComparison.OrdinalIgnoreCase)
            || qTrim.Equals("아무거나", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedGatherTarget
            {
                CollectionId = 0,
                Name = "주변 모든 채집물",
                SceneId = localSceneId,
                SceneName = "",
                ClusterCenter = localPos,
                ClusterNodeCount = 0,
                HasClusterCoords = false
            };
        }

        string escaped = EscapeLuaString(qTrim);
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQ = {escaped}\nlocal curSid = {localSceneId}\nlocal lx = {localPos.X:F2}\nlocal lz = {localPos.Z:F2}\n");

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

        return new ResolvedGatherTarget
        {
            CollectionId = obj["id"]?.GetValue<int>() ?? 0,
            Name = obj["name"]?.GetValue<string>() ?? qTrim,
            SceneId = obj["sceneId"]?.GetValue<int>() ?? localSceneId,
            SceneName = obj["sceneName"]?.GetValue<string>() ?? "",
            HasClusterCoords = obj["hasCluster"]?.GetValue<bool>() == true,
            ClusterCenter = new Position3D(
                obj["x"]?.GetValue<float>() ?? 0f,
                obj["y"]?.GetValue<float>() ?? 0f,
                obj["z"]?.GetValue<float>() ?? 0f),
            ClusterNodeCount = obj["count"]?.GetValue<int>() ?? 0
        };
    }

    public JsonObject ListGatherableResources(string? filter = null)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        string q = EscapeLuaString((filter ?? "").Trim());
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
            inner["ok"] = true;
            inner["autogather_available"] = IsAvailable;
            return inner;
        }
        return res;
    }

    private static string EscapeLuaString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
