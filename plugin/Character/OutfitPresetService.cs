using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Character;
using Stellar.AgyBridge.Core;

namespace Stellar.AgyBridge.Character;

/// <summary>
/// Applies outfit presets saved by the Stellar.WardrobeLoadout plugin
/// (<c>stellar/plugindata/stellar.wardrobeloadout.data/outfits.json</c>, keyed by character id).
/// The file is only READ; outfits are applied through the framework's <see cref="IWardrobe"/> service
/// (+ the same `fashion_setting` Lua WardrobeLoadout uses for hidden regions).
/// Choose a preset by number (1-based) or name; with neither, a random preset (different from the last applied one) is used.
/// </summary>
public sealed class OutfitPresetService
{
    /// <summary>Path of the WardrobeLoadout data file relative to the game directory (single place to change).</summary>
    public static readonly string[] OutfitFileRelativePath =
        { "stellar", "plugindata", "stellar.wardrobeloadout.data", "outfits.json" };

    private sealed record OutfitSlot(
        int Number,
        string Name,
        Dictionary<int, int> Regions,
        Dictionary<int, bool> Hidden,
        int WeaponProfessionId,
        int WeaponSkinId);

    private readonly IPluginServices _services;
    private readonly ICharacterAdapter _character;
    private readonly AsyncTaskRegistry _tasks;
    private readonly MainThreadDispatcher _dispatcher;
    private readonly Random _rng = new();
    private int _lastAppliedNumber;

    public OutfitPresetService(IPluginServices services, ICharacterAdapter character, AsyncTaskRegistry tasks, MainThreadDispatcher dispatcher)
    {
        _services = services;
        _character = character;
        _tasks = tasks;
        _dispatcher = dispatcher;
    }

    private string OutfitFilePath =>
        Path.Combine(new[] { Environment.CurrentDirectory }.Concat(OutfitFileRelativePath).ToArray());

    public JsonObject List()
    {
        if (!TryLoadSlots(out var slots, out string err))
            return new JsonObject { ["ok"] = false, ["error"] = err };

        return new JsonObject
        {
            ["ok"] = true,
            ["char_id"] = _services.PlayerState.CharId,
            ["outfits"] = new JsonArray(slots.Select(s => (JsonNode)new JsonObject
            {
                ["number"] = s.Number,
                ["name"] = s.Name,
                ["regions"] = s.Regions.Count(kv => kv.Value > 0),
                ["weapon_skin_id"] = s.WeaponSkinId
            }).ToArray())
        };
    }

    /// <param name="p">number (1-based) | name (substring) | random (bool); none given → random</param>
    public JsonObject Switch(JsonObject? p)
    {
        if (!_services.Wardrobe.IsAvailable)
            return new JsonObject { ["ok"] = false, ["error"] = "의상 변경 기능을 지금은 사용할 수 없어요" };
        if (!TryLoadSlots(out var slots, out string err))
            return new JsonObject { ["ok"] = false, ["error"] = err };

        int number = p?["number"]?.GetValue<int>() ?? p?["index"]?.GetValue<int>() ?? 0;
        string name = (p?["name"]?.GetValue<string>() ?? "").Trim();

        OutfitSlot? picked;
        bool randomPick = false;
        if (number > 0)
        {
            picked = slots.FirstOrDefault(s => s.Number == number);
            if (picked == null)
                return new JsonObject { ["ok"] = false, ["error"] = $"의상 프리셋 {number}번이 없어요 (총 {slots.Count}개)" };
        }
        else if (!string.IsNullOrEmpty(name))
        {
            picked = slots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                     ?? slots.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (picked == null)
                return new JsonObject { ["ok"] = false, ["error"] = $"'{name}' 이름의 의상 프리셋이 없어요" };
        }
        else
        {
            randomPick = true;
            var pool = slots.Where(s => s.Number != _lastAppliedNumber).ToList();
            if (pool.Count == 0) pool = slots;
            picked = pool[_rng.Next(pool.Count)];
        }

        var slot = picked;
        _lastAppliedNumber = slot.Number;
        string asyncId = _tasks.Start(ApplySlotAsync(slot), r => r);

        return new JsonObject
        {
            ["ok"] = true,
            ["async_id"] = asyncId,
            ["action"] = "switch_outfit",
            ["number"] = slot.Number,
            ["preset_name"] = slot.Name,
            ["random"] = randomPick
        };
    }

    private async Task<JsonObject> ApplySlotAsync(OutfitSlot slot)
    {
        var wardrobe = _services.Wardrobe;
        var result = await wardrobe.ApplyAsync(slot.Regions, CancellationToken.None).ConfigureAwait(false);
        if (result != WardrobeResult.Success)
            return Fail(slot, result);

        if (slot.WeaponSkinId > 0 && slot.WeaponProfessionId > 0)
        {
            var skinRes = await wardrobe.ApplyWeaponSkinAsync(slot.WeaponProfessionId, slot.WeaponSkinId, CancellationToken.None).ConfigureAwait(false);
            if (skinRes != WardrobeResult.Success)
                _services.Log.Warning($"[AgyBridge][Outfit] Weapon skin apply returned {skinRes} for preset '{slot.Name}'");
        }

        if (slot.Hidden.Count > 0)
        {
            try { await _dispatcher.InvokeAsync(() => ApplyHidden(slot.Hidden)).ConfigureAwait(false); }
            catch (Exception ex) { _services.Log.Warning($"[AgyBridge][Outfit] Hidden-region apply failed: {ex.Message}"); }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["result"] = "Success",
            ["number"] = slot.Number,
            ["preset_name"] = slot.Name
        };
    }

    private static JsonObject Fail(OutfitSlot slot, WardrobeResult r) => new()
    {
        ["ok"] = false,
        ["result"] = r.ToString(),
        ["number"] = slot.Number,
        ["preset_name"] = slot.Name,
        ["error"] = r switch
        {
            WardrobeResult.InCombat => "전투 중에는 의상을 바꿀 수 없어요",
            WardrobeResult.PlayerNotInWorld => "지금은 월드에 있지 않아서 의상을 바꿀 수 없어요",
            WardrobeResult.Rejected => "게임에서 의상 변경을 거부했어요",
            WardrobeResult.Timeout => "의상 변경 응답이 지연되었어요",
            _ => $"의상 변경 실패({r})"
        }
    };

    private bool ApplyHidden(Dictionary<int, bool> hidden)
        => _character.ApplyFashionHiddenRegions(hidden);

    private bool TryLoadSlots(out List<OutfitSlot> slots, out string error)
    {
        slots = new List<OutfitSlot>();
        string path = OutfitFilePath;
        if (!File.Exists(path))
        {
            error = "의상 프리셋 파일(WardrobeLoadout)을 찾지 못했어요";
            return false;
        }

        long charId = _services.PlayerState.CharId;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            if (root == null || root[charId.ToString(CultureInfo.InvariantCulture)] is not JsonArray arr)
            {
                error = $"이 캐릭터(UID {charId})에 저장된 의상 프리셋이 없어요";
                return false;
            }

            int n = 0;
            foreach (var node in arr)
            {
                n++;
                if (node is not JsonObject o) continue;
                var regions = new Dictionary<int, int>();
                if (o["Regions"] is JsonObject ro)
                    foreach (var kv in ro)
                        if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rk))
                            regions[rk] = kv.Value?.GetValue<int>() ?? 0;
                if (regions.Values.All(v => v <= 0)) continue; // empty slot

                var hidden = new Dictionary<int, bool>();
                if (o["Hidden"] is JsonObject ho)
                    foreach (var kv in ho)
                        if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hk))
                            hidden[hk] = kv.Value?.GetValue<bool>() ?? false;

                slots.Add(new OutfitSlot(
                    n,
                    o["Name"]?.GetValue<string>() ?? $"의상 {n}",
                    regions,
                    hidden,
                    o["WeaponProfessionId"]?.GetValue<int>() ?? 0,
                    o["WeaponSkinId"]?.GetValue<int>() ?? 0));
            }
        }
        catch (Exception ex)
        {
            error = $"의상 프리셋 파일을 읽지 못했어요: {ex.Message}";
            return false;
        }

        if (slots.Count == 0)
        {
            error = "사용 가능한(비어 있지 않은) 의상 프리셋이 없어요";
            return false;
        }
        error = "";
        return true;
    }
}
