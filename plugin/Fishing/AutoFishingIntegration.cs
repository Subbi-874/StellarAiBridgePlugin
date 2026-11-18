using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Fishing;

public sealed record FishingSpotPoint(
    int SceneId,
    string SceneName,
    int AreaId,
    string AreaName,
    string Landmark,
    int SceneObjectId,
    long Uid,
    Position3D Position,
    float PlayerDistance);

public sealed class ResolvedFishingTarget
{
    public string Query { get; init; } = "";
    public string MatchReason { get; init; } = "";
    public string? MatchedFishName { get; init; }
    public IReadOnlyList<int> PreferredBaitIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<FishingSpotPoint> Candidates { get; init; } = Array.Empty<FishingSpotPoint>();
    public FishingSpotPoint Primary => Candidates[0];
}

/// <summary>
/// Bridges Stellar.AgyBridge to the installed <c>Stellar.AutoFishing</c> plugin in the same AppDomain
/// and dynamically resolves fishing spots, fish species, bait requirements, and rod/bait auto-equipping
/// directly from the game's live tables (<c>FishingAreaTableMgr</c>, <c>FishingTableMgr</c>,
/// <c>SceneObjectTableMgr</c>, <c>SceneObjectEntityGlobalTableMgr</c>, <c>TransferTableMgr</c>).
/// </summary>
public sealed class AutoFishingIntegration
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;

    private object? _pluginInstance;
    private MethodInfo? _miStartAuto;
    private MethodInfo? _miStopAuto;
    private PropertyInfo? _piState;
    private FieldInfo? _fiAutoEnabled;
    private FieldInfo? _fiFishCaught;
    private FieldInfo? _fiFishMissed;
    private FieldInfo? _fiRodChanged;

    public AutoFishingIntegration(IPluginServices services, LuaBridge lua)
    {
        _services = services;
        _lua = lua;
    }

    public bool IsAvailable => EnsureResolved();

    public bool EnsureResolved()
    {
        if (_pluginInstance != null)
            return true;

        try
        {
            object? found = TryFindViaLauncherEntries("Stellar.AutoFishing.Plugin")
                ?? TryFindViaChainloaderRegistry("Stellar.AutoFishing.Plugin");
            if (found == null)
                return false;

            var t = found.GetType();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var startAuto = t.GetMethod("StartAuto", flags);
            var stopAuto = t.GetMethod("StopAuto", flags);
            if (startAuto == null || stopAuto == null)
                return false;

            _pluginInstance = found;
            _miStartAuto = startAuto;
            _miStopAuto = stopAuto;
            _piState = t.GetProperty("State", flags);
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
                    var del = prop.GetValue(entry) as Delegate;
                    if (del?.Target?.GetType().FullName == targetTypeFullName)
                        return del.Target;
                }
                foreach (var fld in eType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!typeof(Delegate).IsAssignableFrom(fld.FieldType)) continue;
                    var del = fld.GetValue(entry) as Delegate;
                    if (del?.Target?.GetType().FullName == targetTypeFullName)
                        return del.Target;
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

    public bool IsFishingAutomationEnabled()
    {
        if (!EnsureResolved()) return false;
        try
        {
            return _fiAutoEnabled?.GetValue(_pluginInstance) is bool b && b;
        }
        catch { return false; }
    }

    /// <summary>0 = NotAtSpot, 1 = Ready, 2 = Running, 3 = Paused, -1 = Unavailable.</summary>
    public int GetRunStateCode()
    {
        if (!EnsureResolved() || _piState == null) return -1;
        try
        {
            var v = _piState.GetValue(_pluginInstance);
            return v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : -1;
        }
        catch { return -1; }
    }

    public string GetRunStateLabel() => GetRunStateCode() switch
    {
        0 => "NotAtSpot",
        1 => "Ready",
        2 => "Running",
        3 => "Paused",
        _ => "Unavailable"
    };

    public int ReadCaughtCount()
    {
        if (!EnsureResolved() || _fiFishCaught == null) return 0;
        try { return Convert.ToInt32(_fiFishCaught.GetValue(_pluginInstance) ?? 0, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    public int ReadMissedCount()
    {
        if (!EnsureResolved() || _fiFishMissed == null) return 0;
        try { return Convert.ToInt32(_fiFishMissed.GetValue(_pluginInstance) ?? 0, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    public int ReadRodsUsedCount()
    {
        if (!EnsureResolved() || _fiRodChanged == null) return 0;
        try { return Convert.ToInt32(_fiRodChanged.GetValue(_pluginInstance) ?? 0, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    public bool StartAutoFishing()
    {
        if (!EnsureResolved() || _miStartAuto == null) return false;
        try
        {
            _miStartAuto.Invoke(_pluginInstance, null);
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
        if (!EnsureResolved() || _miStopAuto == null) return;
        try
        {
            _miStopAuto.Invoke(_pluginInstance, null);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Fishing] StopAuto failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Exits active fishing stance and closes fishing popups cleanly via a Lua coroutine so stopping
    /// never leaves the character locked on the shoreline.
    /// </summary>
    public void ExitFishingStateLua()
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

    /// <summary>
    /// Checks bag & equipped gear for a fishing rod (ItemTableMgr.Type == 1103) and compatible bait
    /// (ItemTableMgr.Type == 1102). If unequipped or wrong bait is selected, kicks off an async
    /// coroutine to equip the best rod and matching bait immediately.
    /// </summary>
    public bool EnsureRodAndBaitReady(IReadOnlyList<int>? preferredBaitIds, out string errorReason, out bool equippingStarted)
    {
        errorReason = "";
        equippingStarted = false;
        if (!_lua.IsReady)
        {
            errorReason = "Lua 런타임이 아직 준비되지 않았어요.";
            return false;
        }

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

local curRodUuid = fd and tonumber(fd.FishingRod or 0) or 0
local curRodDur = (fd and fd.GetFishingRodDurability and tonumber(fd:GetFishingRodDurability() or 0)) or 1
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
          if curRodUuid > 0 and uidNum == curRodUuid then curRodOwned = true end
          if q > bestRodQuality then
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

if bestRodUuid <= 0 and not curRodOwned then
  return {{ ok = false, reason = 'no_rod', error = '가방에 낚싯대가 없어서 낚시를 할 수 없어요.' }}
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
        var res = _lua.Eval(code);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonObject obj)
        {
            errorReason = res["error"]?.GetValue<string>() ?? "낚싯대/미끼 상태 확인에 실패했어요.";
            return false;
        }

        if (obj["ok"]?.GetValue<bool>() != true)
        {
            errorReason = obj["error"]?.GetValue<string>() ?? "낚싯대 또는 미끼가 부족해요.";
            return false;
        }

        long.TryParse(obj["need_rod_uuid"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long rodToEquip);
        int baitToEquip = obj["need_bait_cfg"]?.GetValue<int>() ?? 0;

        if (rodToEquip > 0 || baitToEquip > 0)
        {
            equippingStarted = true;
            _lua.StartCoroutineRpc(string.Create(CultureInfo.InvariantCulture, $@"
local fvm = Z.VMMgr and Z.VMMgr.GetVM('fishing')
if not fvm then return {{ ok = false }} end
local rodUuid = {rodToEquip}
local baitCfg = {baitToEquip}
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
        return true;
    }

    public bool IsInFishingStanceOrUi()
    {
        if (GetRunStateCode() > 0)
            return true;
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

    /// <summary>
    /// Scans active proximity options in <c>interaction_data:GetData()</c> for any fishing point
    /// (cfg/template/override Id == 2200 or buttonId == 5006 or content containing '낚시')
    /// and executes <c>Z.InteractionMgr:BeginInteraction</c>.
    /// </summary>
    public bool TryTriggerFishingInteraction()
    {
        if (!_lua.IsReady) return false;
        var res = _lua.Eval(TriggerFishingInteractionLua);
        return res["ok"]?.GetValue<bool>() == true && res["result"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public ResolvedFishingTarget? ResolveFishingTarget(string? rawQuery, int localSceneId, Position3D localPos)
    {
        if (!_lua.IsReady) return null;

        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQ = {LuaLiteral.Quote((rawQuery ?? "").Trim())}\nlocal curSid = {localSceneId}\nlocal lx = {localPos.X:F2}\nlocal lz = {localPos.Z:F2}\n");

        var res = _lua.Eval(header + SharedCatalogHelperLua + ResolveFishingTargetBodyLua);
        if (res["ok"]?.GetValue<bool>() != true || res["result"] is not JsonObject obj || obj["found"]?.GetValue<bool>() != true)
            return null;

        var candidates = new List<FishingSpotPoint>();
        if (obj["spots"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject s) continue;
                candidates.Add(new FishingSpotPoint(
                    SceneId: s["scene_id"]?.GetValue<int>() ?? 0,
                    SceneName: s["scene_name"]?.GetValue<string>() ?? "",
                    AreaId: s["area_id"]?.GetValue<int>() ?? 0,
                    AreaName: s["area_name"]?.GetValue<string>() ?? "",
                    Landmark: s["landmark"]?.GetValue<string>() ?? "",
                    SceneObjectId: s["sobj_id"]?.GetValue<int>() ?? 0,
                    Uid: s["uid"]?.GetValue<long>() ?? 0L,
                    Position: new Position3D(
                        s["x"]?.GetValue<float>() ?? 0f,
                        s["y"]?.GetValue<float>() ?? 0f,
                        s["z"]?.GetValue<float>() ?? 0f),
                    PlayerDistance: s["dist"]?.GetValue<float>() ?? 0f));
            }
        }
        if (candidates.Count == 0)
            return null;

        var baits = new List<int>();
        if (obj["baits"] is JsonArray barr)
        {
            foreach (var b in barr)
            {
                if (b != null)
                {
                    try { int bid = b.GetValue<int>(); if (bid > 0) baits.Add(bid); } catch { }
                }
            }
        }

        return new ResolvedFishingTarget
        {
            Query = (rawQuery ?? "").Trim(),
            MatchReason = obj["reason"]?.GetValue<string>() ?? "spot",
            MatchedFishName = obj["fish_name"]?.GetValue<string>(),
            PreferredBaitIds = baits,
            Candidates = candidates
        };
    }

    public JsonObject ListFishingSpots(string? filter = null, int localSceneId = 0)
    {
        if (!_lua.IsReady)
            return new JsonObject { ["ok"] = false, ["error"] = "Lua not ready" };

        Position3D pos = _services.PlayerState.IsAvailable ? _services.PlayerState.Position : default;
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"local rawQ = {LuaLiteral.Quote((filter ?? "").Trim())}\nlocal curSid = {localSceneId}\nlocal lx = {pos.X:F2}\nlocal lz = {pos.Z:F2}\n");

        var res = _lua.Eval(header + SharedCatalogHelperLua + ListFishingSpotsBodyLua);
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj)
        {
            obj["ok"] = true;
            obj["autofishing_available"] = IsAvailable;
            return obj;
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
  if txt:find('낚시', 1, true) then return true end
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

    /// <summary>
    /// Dynamically aggregates FishingAreaTableMgr, FishingTableMgr, ItemTableMgr, SceneObjectTableMgr,
    /// SceneObjectEntityGlobalTableMgr, SceneTableMgr, and TransferTableMgr into a unified spot & species catalog.
    /// Shared by both C# runtime and Python pre-v0.9.3 fallback.
    /// </summary>
    public const string SharedCatalogHelperLua = @"
local function norm(s)
  s = tostring(s or ''):lower()
  s = s:gsub('아스테리즈', '아스텔리즈')
  s = s:gsub('순간%s*이동%s*[-–]?%s*', '')
  s = s:gsub('[%s%p·・]+', '')
  return s
end

local function tokenize(raw)
  local cleaned = tostring(raw or ''):lower()
  cleaned = cleaned:gsub('아스테리즈', '아스텔리즈')
  for _, sw in ipairs({
    '낚시터', '낚시 포인트', '낚시포인트', '낚시', '포인트', '할 수 있는 곳',
    '근처', '주변', '여기', '이곳', '이 근처', '가까운', '아무데나', '보유'
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

  -- Discover ALL SceneObjectIds representing a Fishing Point (InteractionTemplate contains 2200 or Name == '낚시 포인트')
  local fishSobjSet = {}
  if sot and sot.GetDatas then
    for id, r in pairs(sot.GetDatas()) do
      local isFish = (tostring(r.Name or '') == '낚시 포인트')
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
            nm = nm:gsub('^순간%s*이동%s*[-–]%s*', '')
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
local hereWords = { ['']=true, ['여기']=true, ['근처']=true, ['주변']=true, ['이곳']=true, ['이근처']=true, ['가까운곳']=true, ['아무데나']=true, ['낚시']=true, ['낚시터']=true }

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

    public const string ListFishingSpotsBodyLua = @"
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
