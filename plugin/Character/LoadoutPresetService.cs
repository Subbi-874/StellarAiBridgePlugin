using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;

namespace Stellar.AgyBridge.Character;

/// <summary>
/// Switches the job / skill / talent / gear preset ("loadout") through the Stellar framework's
/// <see cref="ILoadout"/> service — one call applies profession, talents, skills and equipment together.
/// A preset can be chosen by number (1-based position), by name, or by profession (id or Korean name).
/// </summary>
public sealed class LoadoutPresetService
{
    private readonly IPluginServices _services;
    private readonly LuaBridge _lua;
    private readonly AsyncTaskRegistry _tasks;
    private Dictionary<int, string>? _professionNames;

    public LoadoutPresetService(IPluginServices services, LuaBridge lua, AsyncTaskRegistry tasks)
    {
        _services = services;
        _lua = lua;
        _tasks = tasks;
    }

    public JsonObject List()
    {
        if (!_services.Loadout.IsAvailable)
            return new JsonObject { ["ok"] = false, ["error"] = "Loadout service unavailable (not in world yet?)" };

        var arr = new JsonArray();
        var slots = _services.Loadout.GetSlots();
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            arr.Add(new JsonObject
            {
                ["number"] = i + 1,
                ["index"] = s.Index,
                ["name"] = s.Name,
                ["is_current"] = s.IsCurrent,
                ["profession_id"] = s.ProfessionId,
                ["profession_name"] = ProfessionName(s.ProfessionId)
            });
        }
        return new JsonObject { ["ok"] = true, ["current_index"] = _services.Loadout.CurrentIndex, ["presets"] = arr };
    }

    /// <param name="p">number (1-based) | name (substring) | profession (id or name)</param>
    public JsonObject Switch(JsonObject? p)
    {
        if (!_services.Loadout.IsAvailable)
            return new JsonObject { ["ok"] = false, ["error"] = "직업/스킬 프리셋 기능을 지금은 사용할 수 없어요" };

        var slots = _services.Loadout.GetSlots();
        if (slots.Count == 0)
            return new JsonObject { ["ok"] = false, ["error"] = "저장된 직업/스킬 프리셋이 없어요" };

        int number = p?["number"]?.GetValue<int>() ?? p?["index"]?.GetValue<int>() ?? 0;
        string name = (p?["name"]?.GetValue<string>() ?? "").Trim();
        string profession = (p?["profession"]?.GetValue<string>() ?? "").Trim();

        LoadoutSlot? picked = null;
        if (number > 0)
        {
            if (number > slots.Count)
                return new JsonObject { ["ok"] = false, ["error"] = $"프리셋 {number}번이 없어요 (총 {slots.Count}개)" };
            picked = slots[number - 1];
        }
        else if (!string.IsNullOrEmpty(name))
        {
            picked = slots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                     ?? slots.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrEmpty(profession))
        {
            picked = FindByProfession(slots, profession);
        }
        else
        {
            return new JsonObject { ["ok"] = false, ["error"] = "number(프리셋 번호), name(프리셋 이름), profession(직업) 중 하나가 필요해요" };
        }

        if (picked == null)
            return new JsonObject { ["ok"] = false, ["error"] = "조건에 맞는 직업/스킬 프리셋을 찾지 못했어요" };

        if (picked.IsCurrent)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["already_current"] = true,
                ["preset_name"] = picked.Name,
                ["profession_name"] = ProfessionName(picked.ProfessionId)
            };
        }

        var slot = picked;
        string asyncId = _tasks.Start(
            _services.Loadout.ApplyAsync(slot.Index, CancellationToken.None),
            result => new JsonObject
            {
                ["ok"] = result == LoadoutResult.Success,
                ["result"] = result.ToString(),
                ["preset_name"] = slot.Name,
                ["profession_name"] = ProfessionName(slot.ProfessionId),
                ["error"] = result == LoadoutResult.Success ? null : DescribeFailure(result)
            });

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = "switch_loadout",
            ["preset_name"] = slot.Name,
            ["profession_name"] = ProfessionName(slot.ProfessionId)
        };
    }

    private LoadoutSlot? FindByProfession(IReadOnlyList<LoadoutSlot> slots, string query)
    {
        if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return slots.FirstOrDefault(s => s.ProfessionId == id && !s.IsCurrent) ?? slots.FirstOrDefault(s => s.ProfessionId == id);

        EnsureProfessionNames();
        var matchedIds = new HashSet<int>(_professionNames!
            .Where(kv => kv.Value.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key));
        return slots.FirstOrDefault(s => matchedIds.Contains(s.ProfessionId) && !s.IsCurrent)
               ?? slots.FirstOrDefault(s => matchedIds.Contains(s.ProfessionId));
    }

    private string ProfessionName(int professionId)
    {
        if (professionId <= 0) return "";
        EnsureProfessionNames();
        return _professionNames!.TryGetValue(professionId, out var n) ? n : $"#{professionId}";
    }

    private void EnsureProfessionNames()
    {
        if (_professionNames is { Count: > 0 }) return;
        _professionNames = new Dictionary<int, string>();
        if (!_lua.IsReady) return;

        var res = _lua.Eval(@"
local out = {}
local t = Z.TableMgr.GetTable('ProfessionSystemTableMgr')
if t then for id, r in pairs(t.GetDatas()) do out[tostring(id)] = tostring(r.Name or '') end end
return out
");
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject o)
        {
            foreach (var kv in o)
                if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    _professionNames[id] = kv.Value?.GetValue<string>() ?? "";
        }
    }

    private static string DescribeFailure(LoadoutResult r) => r switch
    {
        LoadoutResult.InCombat => "전투 중에는 프리셋을 바꿀 수 없어요",
        LoadoutResult.PlayerNotInWorld => "지금은 월드에 있지 않아서 프리셋을 바꿀 수 없어요",
        LoadoutResult.NoSuchLoadout => "해당 프리셋이 존재하지 않아요",
        LoadoutResult.Rejected => "게임에서 프리셋 변경을 거부했어요",
        LoadoutResult.Timeout => "프리셋 변경 응답이 지연되었어요",
        LoadoutResult.GameApiUnavailable => "프리셋 기능을 지금은 사용할 수 없어요",
        _ => $"프리셋 변경 실패({r})"
    };
}
