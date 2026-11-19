using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;

namespace Stellar.AgyBridge.Adapters;

/// <summary>
/// Collects 2-stage capability probes (load-time reflection metadata + post-world-entry Lua checks),
/// guards commands whose underlying game API was broken by a patch, and surfaces status to RPC/UI/LLM.
/// </summary>
public sealed class CapabilityRegistry
{
    private const float WorldProbeDelaySec = 1.2f;

    private static readonly Dictionary<string, string> MethodCapabilityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revive"] = CapabilityKeys.Revive,
        ["ride"] = CapabilityKeys.Mount,
        ["party_action"] = CapabilityKeys.Party,
        ["accept_interaction"] = CapabilityKeys.Interaction,
        ["request_interaction"] = CapabilityKeys.Interaction,
        ["request_ride"] = CapabilityKeys.Interaction,
        ["query_player_location"] = CapabilityKeys.PlayerLocate,
        ["list_loadouts"] = CapabilityKeys.Loadout,
        ["switch_loadout"] = CapabilityKeys.Loadout,
        ["list_outfits"] = CapabilityKeys.Outfit,
        ["switch_outfit"] = CapabilityKeys.Outfit,
        ["seek_monster"] = CapabilityKeys.MonsterSeek,
        ["list_dungeons"] = CapabilityKeys.Dungeon,
        ["enter_dungeon"] = CapabilityKeys.Dungeon,
        ["leave_dungeon"] = CapabilityKeys.Dungeon,
        ["ready_check"] = CapabilityKeys.AutoReady,
        ["get_ready_status"] = CapabilityKeys.AutoReady,
        ["list_emotes"] = CapabilityKeys.Emote,
        ["play_emote"] = CapabilityKeys.Emote,
        ["list_stickers"] = CapabilityKeys.Sticker,
        ["send_sticker"] = CapabilityKeys.Sticker,
        ["teleport_place"] = CapabilityKeys.TeleportChannel,
        ["teleport_scene"] = CapabilityKeys.TeleportChannel,
        ["list_channels"] = CapabilityKeys.TeleportChannel,
        ["switch_channel"] = CapabilityKeys.TeleportChannel,
        ["list_gimmicks"] = CapabilityKeys.TraversalGimmick,
        ["use_gimmick"] = CapabilityKeys.TraversalGimmick,
        ["gather_resource"] = CapabilityKeys.AutoGather,
        ["list_gatherables"] = CapabilityKeys.AutoGather,
        ["fish"] = CapabilityKeys.AutoFish,
        ["list_fishing_spots"] = CapabilityKeys.AutoFish,
        ["get_item_count"] = CapabilityKeys.Craft,
        ["get_recipe"] = CapabilityKeys.Craft,
        ["export_recipes"] = CapabilityKeys.Craft,
        ["craft_item"] = CapabilityKeys.Craft,
        ["farm_monster_item"] = CapabilityKeys.MonsterSeek,
        ["reset_camera"] = CapabilityKeys.CameraGuard
    };

    private readonly IPluginLog _log;
    private readonly WorldAccessGuard _guard;
    private readonly ILocalization? _loc;
    private readonly object _lock = new();
    private readonly List<ICapabilityProbe> _probes = new();
    private readonly Dictionary<string, CapabilityEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private bool _worldProbed;
    private float _worldReadyTimer;

    public event Action? CapabilitiesUpdated;

    public CapabilityRegistry(IPluginLog log, WorldAccessGuard guard, ILocalization? localization = null)
    {
        _log = log;
        _guard = guard;
        _loc = localization;
    }

    private string LocFormat(string key, string fallbackTemplate, params object[] args)
    {
        if (_loc != null)
        {
            string val = _loc.TFormat(key, args);
            if (!string.Equals(val, key, StringComparison.Ordinal))
                return val;
        }
        return args.Length == 0 ? fallbackTemplate : string.Format(CultureInfo.InvariantCulture, fallbackTemplate, args);
    }

    public bool HasWorldProbed => _worldProbed;

    public void RegisterProbe(ICapabilityProbe probe)
    {
        lock (_lock)
        {
            _probes.Add(probe);
        }
    }

    public void RunLoadTimeProbes()
    {
        List<ICapabilityProbe> snapshot;
        lock (_lock)
        {
            snapshot = new List<ICapabilityProbe>(_probes);
        }

        var collected = new List<CapabilityEntry>();
        foreach (var probe in snapshot)
        {
            try
            {
                collected.AddRange(probe.ProbeLoadTime());
            }
            catch (Exception ex)
            {
                _log.Warning($"[AgyBridge][Capability] Load-time probe '{probe.Domain}' error: {ex.Message}");
            }
        }

        MergeEntries(collected);
        _log.Info($"[AgyBridge][Capability] Load-time metadata probe recorded {collected.Count} item(s).");
    }

    public void Tick(float deltaTime)
    {
        if (_worldProbed) return;

        if (!_guard.IsLuaWorldReady)
        {
            _worldReadyTimer = 0f;
            return;
        }

        _worldReadyTimer += deltaTime;
        if (_worldReadyTimer < WorldProbeDelaySec)
            return;

        _worldProbed = true;
        RunWorldProbesInternal(logSummary: true);
    }

    public JsonObject ForceReprobeInWorld()
    {
        if (_guard.IsLuaWorldReady)
        {
            _worldProbed = true;
            RunWorldProbesInternal(logSummary: true);
        }
        return BuildStatusJson();
    }

    public JsonObject? CheckCommandCapability(string method)
    {
        if (!MethodCapabilityMap.TryGetValue(method, out string? capKey))
            return null;

        CapabilityEntry? entry;
        bool worldProbed;
        lock (_lock)
        {
            worldProbed = _worldProbed;
            _entries.TryGetValue(capKey, out entry);
        }

        if (entry == null)
            return null;

        // Only enforce Missing if world probe has finished, or load-time inspection explicitly marked it Missing
        if (!worldProbed && entry.State != CapabilityState.Missing)
            return null;

        if (entry.State != CapabilityState.Missing)
            return null;

        string label = CapabilityKeys.GetLabel(entry.Key, _loc);
        return new JsonObject
        {
            ["ok"] = false,
            ["capability_missing"] = true,
            ["capability"] = entry.Key,
            ["label"] = label,
            ["error"] = LocFormat("cap.blocked_fmt", "패치로 사용 불가 ({0}): {1}", label, entry.Detail)
        };
    }

    public string GetUiSummary(out CapabilityState worstState)
    {
        int avail = 0, deg = 0, miss = 0;
        var missingLabels = new List<string>();
        bool worldProbed;

        lock (_lock)
        {
            worldProbed = _worldProbed;
            foreach (var e in _entries.Values)
            {
                switch (e.State)
                {
                    case CapabilityState.Available: avail++; break;
                    case CapabilityState.Degraded: deg++; break;
                    case CapabilityState.Missing:
                        miss++;
                        missingLabels.Add(CapabilityKeys.GetLabel(e.Key, _loc));
                        break;
                }
            }
        }

        if (miss > 0)
        {
            worstState = CapabilityState.Missing;
            string preview = string.Join(", ", missingLabels.Count > 3 ? missingLabels.GetRange(0, 3) : missingLabels);
            string suffix = missingLabels.Count > 3 ? LocFormat("cap.summary.more_fmt", " 외 {0}건", missingLabels.Count - 3) : "";
            return LocFormat("cap.summary.missing_fmt", "자가점검: 사용 불가 {0}건 ({1}{2}) · 정상 {3}", miss, preview, suffix, avail);
        }

        if (!worldProbed)
        {
            worstState = CapabilityState.Unknown;
            return LocFormat("cap.summary.waiting_fmt", "자가점검: 월드 진입 후 Lua 점검 대기 (네이티브 {0}건 확인)", avail);
        }

        if (deg > 0)
        {
            worstState = CapabilityState.Degraded;
            return LocFormat("cap.summary.degraded_fmt", "자가점검: 정상 {0} · 일부저하 {1} (사용 불가 0)", avail, deg);
        }

        worstState = CapabilityState.Available;
        return LocFormat("cap.summary.ok_fmt", "자가점검: 전체 기능 정상 ({0}개 API 확인)", avail);
    }

    public JsonObject BuildStatusJson()
    {
        var itemsArr = new JsonArray();
        var missingKeys = new JsonArray();
        var missingLabels = new JsonArray();
        var degradedLabels = new JsonArray();
        int avail = 0, deg = 0, miss = 0, unk = 0;
        bool worldProbed;

        lock (_lock)
        {
            worldProbed = _worldProbed;
            var ordered = new List<CapabilityEntry>(_entries.Values);
            ordered.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

            foreach (var e in ordered)
            {
                string label = CapabilityKeys.GetLabel(e.Key, _loc);
                string stateStr = e.State.ToString().ToLowerInvariant();
                switch (e.State)
                {
                    case CapabilityState.Available: avail++; break;
                    case CapabilityState.Degraded:
                        deg++;
                        degradedLabels.Add(label);
                        break;
                    case CapabilityState.Missing:
                        miss++;
                        missingKeys.Add(e.Key);
                        missingLabels.Add(label);
                        break;
                    default: unk++; break;
                }

                itemsArr.Add(new JsonObject
                {
                    ["key"] = e.Key,
                    ["label"] = label,
                    ["domain"] = e.Domain,
                    ["state"] = stateStr,
                    ["detail"] = e.Detail
                });
            }
        }

        return new JsonObject
        {
            ["world_probed"] = worldProbed,
            ["available_count"] = avail,
            ["degraded_count"] = deg,
            ["missing_count"] = miss,
            ["unknown_count"] = unk,
            ["missing_keys"] = missingKeys,
            ["missing_labels"] = missingLabels,
            ["degraded_labels"] = degradedLabels,
            ["items"] = itemsArr
        };
    }

    private void RunWorldProbesInternal(bool logSummary)
    {
        List<ICapabilityProbe> snapshot;
        lock (_lock)
        {
            snapshot = new List<ICapabilityProbe>(_probes);
        }

        var collected = new List<CapabilityEntry>();
        foreach (var probe in snapshot)
        {
            try
            {
                collected.AddRange(probe.ProbeInWorld());
            }
            catch (Exception ex)
            {
                _log.Warning($"[AgyBridge][Capability] World probe '{probe.Domain}' error: {ex.Message}");
            }
        }

        MergeEntries(collected);

        if (logSummary)
        {
            string summary = GetUiSummary(out var worst);
            if (worst == CapabilityState.Missing || worst == CapabilityState.Degraded)
                _log.Warning($"[AgyBridge][Capability] {summary}");
            else
                _log.Info($"[AgyBridge][Capability] {summary}");
        }

        CapabilitiesUpdated?.Invoke();
    }

    private void MergeEntries(IEnumerable<CapabilityEntry> entries)
    {
        lock (_lock)
        {
            foreach (var e in entries)
            {
                _entries[e.Key] = e;
            }
        }
    }
}
