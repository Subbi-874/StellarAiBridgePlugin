using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Social;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Resolves and sends owned in-game picture stickers (<see cref="IChat.SendSticker"/>) using
/// Stellar's <see cref="IGameStickerCatalog"/> (<c>_services.GameAssets</c>), filtered dynamically
/// against <c>Z.Global.ChatStickersSort</c> so deprecated/unbundled dummy table groups (whose textures
/// do not exist in client AssetBundles) are never selected, while respecting per-channel AdminLock gates.
/// </summary>
public sealed class StickerService
{
    private readonly IPluginServices _services;
    private readonly ISocialAdapter _social;
    private readonly WorldStateService _world;
    private readonly Random _rng = new();

    private List<StickerInfo> _cachedOwned = new();
    private HashSet<int> _activeStickerGroups = new();
    private long _lastCacheBuildMs;
    private long _cachedForCharId;

    // Semantic mood/intent aliases -> preferred relative index offsets inside each character sticker pack (1..8)
    // so mood tags pick expressive stickers across any unlocked pack without hardcoding single numeric IDs.
    private static readonly Dictionary<string, int[]> MoodSlotPreferences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["greet"]    = new[] { 1, 2 },
        ["hello"]    = new[] { 1, 2 },
        ["인사"]     = new[] { 1, 2 },
        ["반가움"]   = new[] { 1, 2 },
        ["thanks"]   = new[] { 2, 3, 4 },
        ["감사"]     = new[] { 2, 3, 4 },
        ["고마움"]   = new[] { 2, 3, 4 },
        ["thumbsup"] = new[] { 3, 4, 1 },
        ["good"]     = new[] { 3, 4, 1 },
        ["ok"]       = new[] { 1, 3, 4 },
        ["확인"]     = new[] { 1, 3, 4 },
        ["따봉"]     = new[] { 3, 4 },
        ["최고"]     = new[] { 3, 4 },
        ["cheer"]    = new[] { 3, 4, 5 },
        ["응원"]     = new[] { 3, 4, 5 },
        ["축하"]     = new[] { 3, 4, 5 },
        ["heart"]    = new[] { 2, 4, 5 },
        ["love"]     = new[] { 2, 4, 5 },
        ["하트"]     = new[] { 2, 4, 5 },
        ["좋아"]     = new[] { 2, 4, 5 },
        ["laugh"]    = new[] { 5, 3, 2 },
        ["happy"]    = new[] { 2, 3, 5 },
        ["웃음"]     = new[] { 5, 3, 2 },
        ["기쁨"]     = new[] { 2, 3, 5 },
        ["cute"]     = new[] { 1, 2, 4 },
        ["귀여움"]   = new[] { 1, 2, 4 },
        ["think"]    = new[] { 6, 7 },
        ["고민"]     = new[] { 6, 7 },
        ["궁금"]     = new[] { 6, 7 },
        ["sorry"]    = new[] { 7, 6, 8 },
        ["sweat"]    = new[] { 7, 6 },
        ["사과"]     = new[] { 7, 6, 8 },
        ["미안"]     = new[] { 7, 6, 8 },
        ["당황"]     = new[] { 6, 7 },
        ["sad"]      = new[] { 7, 8 },
        ["cry"]      = new[] { 7, 8 },
        ["슬픔"]     = new[] { 7, 8 },
        ["울음"]     = new[] { 7, 8 },
        ["angry"]    = new[] { 8, 6 },
        ["화남"]     = new[] { 8, 6 },
    };

    public StickerService(IPluginServices services, ISocialAdapter social, WorldStateService world)
    {
        _services = services;
        _social = social;
        _world = world;
    }

    private void RefreshActiveStickerGroups()
    {
        var groups = _social.QueryActiveStickerGroups();
        if (groups.Count == 0)
            return;

        _activeStickerGroups = new HashSet<int>(groups);
    }

    public IReadOnlyList<StickerInfo> GetOwnedPictureStickers(bool forceRefresh = false)
    {
        long now = Environment.TickCount64;
        long selfCid = _services.PlayerState.CharId;
        if (!forceRefresh
            && _cachedOwned.Count > 0
            && _activeStickerGroups.Count > 0
            && _cachedForCharId == selfCid
            && now - _lastCacheBuildMs < 30_000)
        {
            return _cachedOwned;
        }

        if (forceRefresh || _activeStickerGroups.Count == 0)
            RefreshActiveStickerGroups();

        var all = _services.GameAssets.GetSendableStickers();
        if (all == null || all.Count == 0)
            return _cachedOwned;

        var owned = new List<StickerInfo>();
        foreach (var s in all)
        {
            // Type 3 (EPicture) & Type 4 (EHeadPicture) are true ChatPictureEmoji stickers sendable via IChat.SendSticker
            if (s.Type is not (3 or 4))
                continue;
            if (string.IsNullOrWhiteSpace(s.Res))
                continue;
            if (_activeStickerGroups.Count > 0 && !_activeStickerGroups.Contains(s.GroupId))
                continue;
            bool isOwned = s.DefaultUnlocked || _services.GameAssets.IsStickerOwned(s.ConfigId);
            if (isOwned)
                owned.Add(s);
        }

        owned.Sort((a, b) => a.ConfigId.CompareTo(b.ConfigId));
        if (owned.Count > 0)
        {
            _cachedOwned = owned;
            _cachedForCharId = selfCid;
            _lastCacheBuildMs = now;
        }
        return _cachedOwned;
    }

    public JsonObject ListStickers(JsonObject? p)
    {
        string q = (p?["query"]?.GetValue<string>() ?? "").Trim();
        int groupFilter = p?["group_id"]?.GetValue<int>() ?? 0;
        var owned = GetOwnedPictureStickers(p?["force_refresh"]?.GetValue<bool>() ?? false);

        var arr = new JsonArray();
        foreach (var s in owned)
        {
            if (groupFilter > 0 && s.GroupId != groupFilter) continue;
            if (!string.IsNullOrEmpty(q)
                && !s.Res.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !s.ConfigId.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal))
            {
                continue;
            }
            arr.Add(new JsonObject
            {
                ["id"] = s.ConfigId,
                ["type"] = s.Type,
                ["group_id"] = s.GroupId,
                ["res"] = s.Res,
                ["default_unlocked"] = s.DefaultUnlocked
            });
            if (arr.Count >= 120) break;
        }

        var moods = new JsonArray();
        foreach (var key in new[] { "greet", "thanks", "thumbsup", "cheer", "heart", "laugh", "cute", "ok", "think", "sorry", "sad", "random" })
            moods.Add(key);

        return new JsonObject
        {
            ["ok"] = true,
            ["owned_count"] = owned.Count,
            ["supported_moods"] = moods,
            ["stickers"] = arr
        };
    }

    public JsonObject Send(JsonObject? p)
    {
        string channel = p?["channel"]?.GetValue<string>() ?? "party";
        long targetCid = p?["target_char_id"]?.GetValue<long>() ?? 0L;
        int explicitId = p?["sticker_id"]?.GetValue<int>() ?? p?["id"]?.GetValue<int>() ?? 0;
        string query = (p?["sticker"]?.GetValue<string>() ?? p?["mood"]?.GetValue<string>() ?? p?["query"]?.GetValue<string>() ?? "").Trim();

        var owned = GetOwnedPictureStickers();
        if (owned.Count == 0)
            return new JsonObject { ["ok"] = false, ["error"] = "현재 사용할 수 있는 보유 이모티콘(스티커)이 없어요" };

        StickerInfo? picked = ResolveSticker(owned, explicitId, query);
        if (picked == null)
            return new JsonObject { ["ok"] = false, ["error"] = $"요청한 이모티콘('{query}')을 보유 목록에서 찾지 못했어요" };

        bool sent = _world.SendSticker(channel, picked.ConfigId, targetCid, out string blockReason, out double remCd);
        var resp = new JsonObject
        {
            ["ok"] = sent,
            ["channel"] = channel,
            ["sticker_id"] = picked.ConfigId,
            ["res"] = picked.Res,
            ["group_id"] = picked.GroupId,
            ["remaining_cooldown_sec"] = Math.Round(remCd, 2)
        };
        if (!sent)
            resp["error"] = blockReason;
        return resp;
    }

    private StickerInfo? ResolveSticker(IReadOnlyList<StickerInfo> owned, int explicitId, string query)
    {
        if (explicitId > 0)
        {
            foreach (var s in owned)
                if (s.ConfigId == explicitId) return s;
        }

        if (!string.IsNullOrEmpty(query) && int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId) && parsedId > 0)
        {
            foreach (var s in owned)
                if (s.ConfigId == parsedId) return s;
        }

        if (!string.IsNullOrEmpty(query)
            && !query.Equals("random", StringComparison.OrdinalIgnoreCase)
            && !query.Equals("아무거나", StringComparison.OrdinalIgnoreCase)
            && !query.Equals("랜덤", StringComparison.OrdinalIgnoreCase))
        {
            // 1. Direct resource name match (e.g. "sticker_7_1", "emoji_2_1")
            var resMatches = owned.Where(s => s.Res.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (resMatches.Count > 0)
                return resMatches[_rng.Next(resMatches.Count)];

            // 2. Semantic mood / Korean keyword match across owned sticker groups
            int[]? preferredOffsets = null;
            if (!MoodSlotPreferences.TryGetValue(query, out preferredOffsets))
            {
                foreach (var kv in MoodSlotPreferences)
                {
                    if (query.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        preferredOffsets = kv.Value;
                        break;
                    }
                }
            }

            if (preferredOffsets != null && preferredOffsets.Length > 0)
            {
                var byGroup = owned
                    .GroupBy(s => s.GroupId)
                    .Select(g => g.OrderBy(x => x.ConfigId).ToList())
                    .Where(g => g.Count > 0)
                    .ToList();

                var candidates = new List<StickerInfo>();
                foreach (var grp in byGroup)
                {
                    foreach (int oneBased in preferredOffsets)
                    {
                        int idx = oneBased - 1;
                        if (idx >= 0 && idx < grp.Count)
                            candidates.Add(grp[idx]);
                    }
                }
                if (candidates.Count > 0)
                    return candidates[_rng.Next(candidates.Count)];
            }
        }

        return owned[_rng.Next(owned.Count)];
    }
}
