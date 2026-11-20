using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;

namespace Stellar.AgyBridge.Bridge.Planning;

/// <summary>
/// Pure deterministic multi-stage crafting dependency planner (1:1 port of <c>mcp_bridge/craft_planner.py</c>).
/// </summary>
public sealed class CraftPlanner
{
    private static readonly Regex RecipeNormStripRegex = new(@"[\W_\u200b\ufeff]+", RegexOptions.Compiled);

    public static string NormalizeRecipeName(string? text)
        => RecipeNormStripRegex.Replace((text ?? "").ToLowerInvariant(), "");

    public static string[] TokenizeRecipeName(string? text)
        => RecipeNormStripRegex.Split((text ?? "").ToLowerInvariant())
            .Where(tok => !string.IsNullOrEmpty(tok))
            .ToArray();

    /// <summary>
    /// Computes Ratcliff/Obershelp similarity matching Python <c>difflib.SequenceMatcher(None, a, b).ratio()</c>.
    /// </summary>
    public static double SequenceMatcherRatio(string a, string b)
    {
        int totalLen = a.Length + b.Length;
        if (totalLen == 0)
            return 1.0;
        if (a.Length == 0 || b.Length == 0)
            return 0.0;

        int matches = 0;
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Length, 0, b.Length));

        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = FindLongestMatch(a, alo, ahi, b, blo, bhi);
            if (k == 0)
                continue;
            matches += k;
            if (alo < i && blo < j)
                queue.Push((alo, i, blo, j));
            if (i + k < ahi && j + k < bhi)
                queue.Push((i + k, ahi, j + k, bhi));
        }

        return (2.0 * matches) / totalLen;
    }

    private static (int I, int J, int Size) FindLongestMatch(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        int bestI = alo;
        int bestJ = blo;
        int bestSize = 0;
        int bSpan = bhi - blo;
        int[] prev = new int[bSpan + 1];
        int[] curr = new int[bSpan + 1];

        for (int ia = alo; ia < ahi; ia++)
        {
            char ca = a[ia];
            for (int jb = 0; jb < bSpan; jb++)
            {
                if (ca == b[blo + jb])
                {
                    int k = prev[jb] + 1;
                    curr[jb + 1] = k;
                    if (k > bestSize)
                    {
                        bestSize = k;
                        bestI = ia - k + 1;
                        bestJ = blo + jb - k + 1;
                    }
                }
                else
                {
                    curr[jb + 1] = 0;
                }
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }

        return (bestI, bestJ, bestSize);
    }

    public static JsonObject? SelectTargetRecipe(
        IReadOnlyList<JsonObject> recipes,
        int recipeId = 0,
        string query = "",
        IReadOnlyDictionary<int, bool>? unlockMap = null)
    {
        if (recipeId > 0)
        {
            foreach (JsonObject r in recipes)
            {
                if (JsonNodeHelpers.GetInt(r, "recipe_id", 0) == recipeId)
                    return r;
            }
            return null;
        }

        string qClean = (query ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(qClean))
            return null;
        string qNorm = NormalizeRecipeName(query);
        string[] qTokens = TokenizeRecipeName(query);

        var cands = new List<(int UnlockedRank, int MatchRank, int LenRank, int Rid, JsonObject Recipe)>();
        foreach (JsonObject r in recipes)
        {
            int rid = JsonNodeHelpers.GetInt(r, "recipe_id", 0);
            string name = JsonNodeHelpers.GetString(r, "name", "").Trim();
            string outName = JsonNodeHelpers.GetString(r, "output_name", "").Trim();
            string nLow = name.ToLowerInvariant();
            string oLow = outName.ToLowerInvariant();
            string nNorm = NormalizeRecipeName(name);
            string oNorm = NormalizeRecipeName(outName);

            bool exactRaw = nLow == qClean || oLow == qClean;
            bool exactNorm = !string.IsNullOrEmpty(qNorm) && (nNorm == qNorm || oNorm == qNorm);
            bool partialRaw = nLow.Contains(qClean, StringComparison.Ordinal) || oLow.Contains(qClean, StringComparison.Ordinal);
            bool partialNorm = !string.IsNullOrEmpty(qNorm)
                && (nNorm.Contains(qNorm, StringComparison.Ordinal) || oNorm.Contains(qNorm, StringComparison.Ordinal));
            bool allTokens = qTokens.Length >= 2 && (
                qTokens.All(tok => nNorm.Contains(tok, StringComparison.Ordinal))
                || qTokens.All(tok => oNorm.Contains(tok, StringComparison.Ordinal)));

            int matchRank;
            if (exactRaw || exactNorm)
                matchRank = 0;
            else if (partialRaw || partialNorm)
                matchRank = 1;
            else if (allTokens)
                matchRank = 2;
            else
                continue;

            int unlockedRank = 0;
            if (unlockMap != null && unlockMap.TryGetValue(rid, out bool unlocked))
                unlockedRank = unlocked ? 0 : 1;

            cands.Add((unlockedRank, matchRank, nNorm.Length > 0 ? nNorm.Length : name.Length, rid, r));
        }

        if (cands.Count > 0)
        {
            return cands
                .OrderBy(c => c.UnlockedRank)
                .ThenBy(c => c.MatchRank)
                .ThenBy(c => c.LenRank)
                .ThenBy(c => c.Rid)
                .First().Recipe;
        }

        if (qNorm.Length < 2)
            return null;

        var fuzzyCands = new List<(int UnlockedRank, double NegRatio, int LenRank, int Rid, JsonObject Recipe)>();
        foreach (JsonObject r in recipes)
        {
            int rid = JsonNodeHelpers.GetInt(r, "recipe_id", 0);
            string name = JsonNodeHelpers.GetString(r, "name", "").Trim();
            string outName = JsonNodeHelpers.GetString(r, "output_name", "").Trim();
            string nNorm = NormalizeRecipeName(name);
            string oNorm = NormalizeRecipeName(outName);

            double bestRatio = 0.0;
            if (!string.IsNullOrEmpty(nNorm))
                bestRatio = Math.Max(bestRatio, SequenceMatcherRatio(qNorm, nNorm));
            if (!string.IsNullOrEmpty(oNorm))
                bestRatio = Math.Max(bestRatio, SequenceMatcherRatio(qNorm, oNorm));

            if (bestRatio < 0.72)
                continue;

            int unlockedRank = 0;
            if (unlockMap != null && unlockMap.TryGetValue(rid, out bool unlocked))
                unlockedRank = unlocked ? 0 : 1;

            double roundedNeg = -Math.Round(bestRatio, 4, MidpointRounding.ToEven);
            fuzzyCands.Add((unlockedRank, roundedNeg, nNorm.Length > 0 ? nNorm.Length : name.Length, rid, r));
        }

        if (fuzzyCands.Count == 0)
            return null;

        return fuzzyCands
            .OrderBy(c => c.UnlockedRank)
            .ThenBy(c => c.NegRatio)
            .ThenBy(c => c.LenRank)
            .ThenBy(c => c.Rid)
            .First().Recipe;
    }

    public static List<int> CollectCandidateItemIds(
        IReadOnlyList<JsonObject> recipes,
        JsonObject rootRecipe,
        int maxDepth = 5)
    {
        var byOutput = new Dictionary<int, List<JsonObject>>();
        foreach (JsonObject r in recipes)
        {
            int oid = JsonNodeHelpers.GetInt(r, "output_item_id", 0);
            if (oid > 0)
            {
                if (!byOutput.TryGetValue(oid, out List<JsonObject>? list))
                {
                    list = new List<JsonObject>();
                    byOutput[oid] = list;
                }
                list.Add(r);
            }
        }

        var ids = new HashSet<int>();
        var visitedRecipes = new HashSet<int>();

        void Walk(JsonObject rec, int depth)
        {
            int rid = JsonNodeHelpers.GetInt(rec, "recipe_id", 0);
            if (!visitedRecipes.Add(rid) || depth > maxDepth)
                return;

            int oid = JsonNodeHelpers.GetInt(rec, "output_item_id", 0);
            if (oid > 0)
                ids.Add(oid);

            JsonArray? materials = JsonNodeHelpers.GetArray(rec, "materials");
            if (materials != null)
            {
                foreach (JsonNode? matNode in materials)
                {
                    if (matNode is not JsonObject m)
                        continue;
                    int mid = JsonNodeHelpers.GetInt(m, "item_id", 0);
                    if (mid > 0)
                    {
                        ids.Add(mid);
                        if (byOutput.TryGetValue(mid, out List<JsonObject>? subList))
                        {
                            foreach (JsonObject subR in subList)
                                Walk(subR, depth + 1);
                        }
                    }

                    JsonArray? candArr = JsonNodeHelpers.GetArray(m, "candidate_item_ids");
                    if (candArr != null)
                    {
                        foreach (JsonNode? cidNode in candArr)
                        {
                            if (JsonNodeHelpers.TryGetInt(cidNode, out int cInt) && cInt > 0)
                                ids.Add(cInt);
                        }
                    }
                }
            }

            JsonArray? baseEquips = JsonNodeHelpers.GetArray(rec, "base_equip_candidates");
            if (baseEquips != null)
            {
                foreach (JsonNode? cidNode in baseEquips)
                {
                    if (JsonNodeHelpers.TryGetInt(cidNode, out int cInt) && cInt > 0)
                        ids.Add(cInt);
                }
            }
        }

        Walk(rootRecipe, 0);
        return ids.OrderBy(id => id).ToList();
    }

    public CraftPlanResult PlanCraft(
        IReadOnlyList<JsonObject> recipes,
        IReadOnlyDictionary<int, JsonObject> obtainWays,
        IReadOnlyDictionary<int, int> bagCounts,
        int recipeId = 0,
        string query = "",
        int requestedCount = 1,
        string countMode = "additional",
        bool allowMonsterFarm = false,
        int availableFocus = -1,
        IReadOnlyDictionary<int, bool>? unlockMap = null,
        IReadOnlyDictionary<int, int>? unequippedBaseEquipCounts = null,
        string lang = "ko")
    {
        int reqCnt = Math.Max(1, requestedCount <= 0 ? 1 : requestedCount);
        JsonObject? root = SelectTargetRecipe(recipes, recipeId, query, unlockMap);
        if (root == null)
        {
            string label = !string.IsNullOrWhiteSpace(query) ? query.Trim() : $"#{recipeId}";
            return new CraftPlanResult
            {
                Status = "not_found",
                RequestedCount = reqCnt,
                Message = BridgeI18n.Format("craft_not_found", lang, ("label", label)),
            };
        }

        int rootOutId = JsonNodeHelpers.GetInt(root, "output_item_id", 0);
        int rootYield = Math.Max(1, JsonNodeHelpers.GetInt(root, "yield_per_craft", 1));
        var initialBag = new Dictionary<int, int>();
        foreach (KeyValuePair<int, int> kv in bagCounts)
            initialBag[kv.Key] = Math.Max(0, kv.Value);
        var virtualBag = new Dictionary<int, int>(initialBag);

        int rootCrafts;
        if (string.Equals(countMode, "total_target", StringComparison.Ordinal) && rootOutId > 0)
        {
            int rootOwned = virtualBag.GetValueOrDefault(rootOutId, 0);
            int rootDeficit = Math.Max(0, reqCnt - rootOwned);
            if (rootDeficit == 0)
            {
                string dispName = JsonNodeHelpers.GetString(root, "output_name", "");
                if (string.IsNullOrEmpty(dispName))
                    dispName = JsonNodeHelpers.GetString(root, "name", "");
                return new CraftPlanResult
                {
                    Status = "already_satisfied",
                    TargetRecipe = root,
                    RequestedCount = reqCnt,
                    CraftsNeeded = 0,
                    Message = BridgeI18n.Format("craft_already_owned", lang, ("name", dispName), ("owned", rootOwned)),
                };
            }
            rootCrafts = CeilDiv(rootDeficit, rootYield);
        }
        else
        {
            rootCrafts = CeilDiv(reqCnt, rootYield);
        }

        var byOutput = new Dictionary<int, List<JsonObject>>();
        foreach (JsonObject r in recipes)
        {
            int oid = JsonNodeHelpers.GetInt(r, "output_item_id", 0);
            if (oid > 0)
            {
                if (!byOutput.TryGetValue(oid, out List<JsonObject>? list))
                {
                    list = new List<JsonObject>();
                    byOutput[oid] = list;
                }
                list.Add(r);
            }
        }
        foreach (int oid in byOutput.Keys.ToList())
        {
            byOutput[oid] = byOutput[oid]
                .OrderBy(r =>
                {
                    int rRid = JsonNodeHelpers.GetInt(r, "recipe_id", 0);
                    return (unlockMap == null || !unlockMap.TryGetValue(rRid, out bool u) || u) ? 0 : 1;
                })
                .ThenBy(r => JsonNodeHelpers.GetInt(r, "recipe_id", 0))
                .ToList();
        }

        var steps = new List<GoalStep>();
        var monsterDeficits = new Dictionary<int, int>();
        var monsterOrder = new List<int>();
        var monsterMeta = new Dictionary<int, (string ItemLabel, int MonsterId, string MonsterName)>();
        var blocked = new List<BlockedMaterial>();
        int focusTotal = 0;

        (int Used, int Deficit) ConsumeFromBag(int itemId, int needQty)
        {
            int have = virtualBag.GetValueOrDefault(itemId, 0);
            int take = Math.Min(have, needQty);
            virtualBag[itemId] = have - take;
            return (take, needQty - take);
        }

        JsonObject? PickSubRecipe(int itemId, HashSet<int> visitingItems)
        {
            if (visitingItems.Contains(itemId))
                return null;
            if (!byOutput.TryGetValue(itemId, out List<JsonObject>? cands))
                return null;
            foreach (JsonObject cand in cands)
            {
                int cid = JsonNodeHelpers.GetInt(cand, "recipe_id", 0);
                if (unlockMap != null && unlockMap.TryGetValue(cid, out bool unlocked) && !unlocked)
                    continue;
                return cand;
            }
            return null;
        }

        void ExpandRecipe(JsonObject rec, int craftsCnt, HashSet<int> visitingItems)
        {
            int rid = JsonNodeHelpers.GetInt(rec, "recipe_id", 0);
            string rname = JsonNodeHelpers.GetString(rec, "name", "");
            if (string.IsNullOrEmpty(rname))
                rname = $"Recipe#{rid}";
            string rkind = JsonNodeHelpers.GetString(rec, "recipe_kind", "");
            if (string.IsNullOrEmpty(rkind))
                rkind = "life_build";
            int oid = JsonNodeHelpers.GetInt(rec, "output_item_id", 0);
            int ryield = Math.Max(1, JsonNodeHelpers.GetInt(rec, "yield_per_craft", 1));
            int fcost = Math.Max(0, JsonNodeHelpers.GetInt(rec, "focus_cost", 0));
            focusTotal += fcost * craftsCnt;

            if (unlockMap != null && unlockMap.TryGetValue(rid, out bool rootUnlocked) && !rootUnlocked)
            {
                blocked.Add(new BlockedMaterial
                {
                    ItemId = oid,
                    ItemName = rname,
                    Needed = craftsCnt * ryield,
                    Owned = initialBag.GetValueOrDefault(oid, 0),
                    Deficit = craftsCnt * ryield,
                    Reason = "recipe_locked",
                    ObtainHints = new List<string> { BridgeI18n.T("craft_recipe_locked", lang) },
                });
                return;
            }

            if (rkind == "equip_create" && JsonNodeHelpers.GetInt(rec, "equip_name_group_id", 0) > 0)
            {
                if (unequippedBaseEquipCounts != null)
                {
                    int baseHave = unequippedBaseEquipCounts.GetValueOrDefault(rid, 0);
                    if (baseHave < craftsCnt)
                    {
                        blocked.Add(new BlockedMaterial
                        {
                            ItemId = oid,
                            ItemName = rname,
                            Needed = craftsCnt,
                            Owned = baseHave,
                            Deficit = craftsCnt - baseHave,
                            Reason = "missing_base_equip",
                            ObtainHints = new List<string> { BridgeI18n.T("craft_missing_base_equip", lang) },
                        });
                        return;
                    }
                }
            }

            var nextVisiting = new HashSet<int>(visitingItems);
            if (oid > 0)
                nextVisiting.Add(oid);

            JsonArray? mats = JsonNodeHelpers.GetArray(rec, "materials");
            if (mats != null)
            {
                foreach (JsonNode? matNode in mats)
                {
                    if (matNode is not JsonObject mat)
                        continue;

                    int perNeed = Math.Max(1, JsonNodeHelpers.GetInt(mat, "count", 1));
                    int totalNeed = perNeed * craftsCnt;
                    int mid = JsonNodeHelpers.GetInt(mat, "item_id", 0);
                    string mname = JsonNodeHelpers.GetString(mat, "item_name", "");
                    if (string.IsNullOrEmpty(mname))
                        mname = $"Item#{mid}";

                    int fishTb = JsonNodeHelpers.GetInt(mat, "fish_type_b", 0);
                    if (mid == 0 && fishTb > 0)
                    {
                        var cands = new List<int>();
                        JsonArray? candArr = JsonNodeHelpers.GetArray(mat, "candidate_item_ids");
                        if (candArr != null)
                        {
                            foreach (JsonNode? x in candArr)
                            {
                                if (JsonNodeHelpers.TryGetInt(x, out int cVal) && cVal > 0)
                                    cands.Add(cVal);
                            }
                        }

                        int rem = totalNeed;
                        foreach (int cid in cands)
                        {
                            (_, rem) = ConsumeFromBag(cid, rem);
                            if (rem <= 0)
                                break;
                        }

                        if (rem > 0 && cands.Count > 0)
                        {
                            int pickId = cands[0];
                            obtainWays.TryGetValue(pickId, out JsonObject? owFish);
                            JsonObject? fishInfo = JsonNodeHelpers.GetObject(owFish, "fish");
                            string spotName = JsonNodeHelpers.GetString(fishInfo, "name", "");
                            if (string.IsNullOrEmpty(spotName))
                                spotName = JsonNodeHelpers.GetString(owFish, "item_name", "");
                            if (string.IsNullOrEmpty(spotName))
                                spotName = $"어류#{fishTb}";

                            steps.Add(new GoalStep
                            {
                                ActionType = "fish",
                                Params = new JsonObject { ["spot"] = spotName, ["count"] = rem },
                                TargetCount = rem,
                                BehaviorKind = GoalHelpers.InferBehaviorKind("fish"),
                            });
                        }
                        else if (rem > 0)
                        {
                            blocked.Add(new BlockedMaterial
                            {
                                ItemId = 0,
                                ItemName = mname,
                                Needed = totalNeed,
                                Owned = totalNeed - rem,
                                Deficit = rem,
                                Reason = "unknown_fish_category",
                                ObtainHints = new List<string> { BridgeI18n.T("craft_fish_grade_missing", lang) },
                            });
                        }
                        continue;
                    }

                    if (mid <= 0)
                        continue;

                    (int usedFromBag, int deficit) = ConsumeFromBag(mid, totalNeed);
                    if (deficit <= 0)
                        continue;

                    // 1. Sub-recipe craftable
                    JsonObject? subRec = PickSubRecipe(mid, nextVisiting);
                    if (subRec != null)
                    {
                        int subYield = Math.Max(1, JsonNodeHelpers.GetInt(subRec, "yield_per_craft", 1));
                        int subCrafts = CeilDiv(deficit, subYield);
                        ExpandRecipe(subRec, subCrafts, nextVisiting);
                        int surplus = subCrafts * subYield - deficit;
                        if (surplus > 0)
                            virtualBag[mid] = virtualBag.GetValueOrDefault(mid, 0) + surplus;
                        continue;
                    }

                    // 2. Obtain way resolution (gather > fish > monster > blocked)
                    obtainWays.TryGetValue(mid, out JsonObject? ow);
                    string itemLabel = JsonNodeHelpers.GetString(ow, "item_name", "");
                    if (string.IsNullOrEmpty(itemLabel))
                        itemLabel = mname;

                    JsonObject? gather = JsonNodeHelpers.GetObject(ow, "gather");
                    JsonObject? fish = JsonNodeHelpers.GetObject(ow, "fish");
                    JsonObject? monster = JsonNodeHelpers.GetObject(ow, "monster");
                    var otherWays = new List<string>();
                    JsonArray? otherArr = JsonNodeHelpers.GetArray(ow, "other_ways");
                    if (otherArr != null)
                    {
                        foreach (JsonNode? n in otherArr)
                        {
                            string s = JsonNodeHelpers.AsString(n, "");
                            if (!string.IsNullOrEmpty(s))
                                otherWays.Add(s);
                        }
                    }

                    string resName = JsonNodeHelpers.GetString(gather, "resource_name", "").Trim();
                    if (!string.IsNullOrEmpty(resName))
                    {
                        steps.Add(new GoalStep
                        {
                            ActionType = "gather_resource",
                            Params = new JsonObject { ["resource"] = resName, ["count"] = deficit },
                            TargetCount = deficit,
                            BehaviorKind = GoalHelpers.InferBehaviorKind("gather_resource"),
                        });
                        continue;
                    }

                    string fishName = JsonNodeHelpers.GetString(fish, "name", "").Trim();
                    if (!string.IsNullOrEmpty(fishName))
                    {
                        steps.Add(new GoalStep
                        {
                            ActionType = "fish",
                            Params = new JsonObject { ["spot"] = fishName, ["count"] = deficit },
                            TargetCount = deficit,
                            BehaviorKind = GoalHelpers.InferBehaviorKind("fish"),
                        });
                        continue;
                    }

                    string monName = JsonNodeHelpers.GetString(monster, "monster_name", "").Trim();
                    if (!string.IsNullOrEmpty(monName))
                    {
                        int monId = JsonNodeHelpers.GetInt(monster, "monster_id", 0);
                        if (!monsterDeficits.ContainsKey(mid))
                            monsterOrder.Add(mid);
                        monsterDeficits[mid] = monsterDeficits.GetValueOrDefault(mid, 0) + deficit;
                        monsterMeta[mid] = (itemLabel, monId, monName);
                        if (allowMonsterFarm)
                        {
                            int targetTotal = initialBag.GetValueOrDefault(mid, 0) + monsterDeficits[mid];
                            steps.Add(new GoalStep
                            {
                                ActionType = "farm_monster_item",
                                Params = new JsonObject
                                {
                                    ["monster"] = monName,
                                    ["item_id"] = mid,
                                    ["count"] = targetTotal,
                                    ["target_count"] = targetTotal,
                                },
                                TargetCount = targetTotal,
                                BehaviorKind = GoalHelpers.InferBehaviorKind("farm_monster_item"),
                            });
                        }
                        continue;
                    }

                    bool hasLockedSub = byOutput.TryGetValue(mid, out List<JsonObject>? subCands)
                        && subCands.Any(c =>
                        {
                            int cRid = JsonNodeHelpers.GetInt(c, "recipe_id", 0);
                            return unlockMap != null && unlockMap.TryGetValue(cRid, out bool u) && !u;
                        });

                    string reason = hasLockedSub ? "sub_recipe_locked" : "non_automatable_source";
                    List<string> hints = otherWays.Count > 0
                        ? otherWays
                        : new List<string>
                        {
                            BridgeI18n.T(hasLockedSub ? "craft_sub_recipe_locked" : "craft_external_source_hint", lang),
                        };

                    blocked.Add(new BlockedMaterial
                    {
                        ItemId = mid,
                        ItemName = itemLabel,
                        Needed = totalNeed,
                        Owned = usedFromBag,
                        Deficit = deficit,
                        Reason = reason,
                        ObtainHints = hints,
                    });
                }
            }

            steps.Add(new GoalStep
            {
                ActionType = "craft_item",
                Params = new JsonObject
                {
                    ["recipe_id"] = rid,
                    ["query"] = rname,
                    ["recipe_kind"] = rkind,
                    ["count"] = craftsCnt,
                },
                TargetCount = craftsCnt,
                BehaviorKind = GoalHelpers.InferBehaviorKind("craft_item"),
            });
        }

        ExpandRecipe(root, rootCrafts, new HashSet<int>());

        if (availableFocus >= 0 && focusTotal > availableFocus)
        {
            blocked.Add(new BlockedMaterial
            {
                ItemId = 20003,
                ItemName = BridgeI18n.T("craft_focus_name", lang),
                Needed = focusTotal,
                Owned = availableFocus,
                Deficit = focusTotal - availableFocus,
                Reason = "insufficient_focus",
                ObtainHints = new List<string>
                {
                    BridgeI18n.Format("craft_focus_shortage", lang, ("needed", focusTotal), ("owned", availableFocus)),
                },
            });
        }

        if (blocked.Count > 0)
        {
            BlockedMaterial firstB = blocked[0];
            return new CraftPlanResult
            {
                Status = "blocked",
                TargetRecipe = root,
                RequestedCount = reqCnt,
                CraftsNeeded = rootCrafts,
                TotalFocusNeeded = focusTotal,
                BlockedMaterials = blocked,
                Message = BridgeI18n.Format(
                    "craft_blocked",
                    lang,
                    ("item_name", firstB.ItemName),
                    ("deficit", firstB.Deficit),
                    ("hints", string.Join(", ", firstB.ObtainHints))),
            };
        }

        if (monsterDeficits.Count > 0 && !allowMonsterFarm)
        {
            var prompts = new List<MonsterFarmPrompt>(monsterOrder.Count);
            foreach (int mid in monsterOrder)
            {
                (string iLabel, int monId, string monName) = monsterMeta[mid];
                prompts.Add(new MonsterFarmPrompt
                {
                    ItemId = mid,
                    ItemName = iLabel,
                    Deficit = monsterDeficits[mid],
                    MonsterId = monId,
                    MonsterName = monName,
                });
            }

            string desc = string.Join(", ", prompts.Select(p =>
                BridgeI18n.Format(
                    "craft_monster_item_fmt",
                    lang,
                    ("item_name", p.ItemName),
                    ("deficit", p.Deficit),
                    ("monster_name", p.MonsterName))));

            return new CraftPlanResult
            {
                Status = "needs_monster_confirm",
                TargetRecipe = root,
                RequestedCount = reqCnt,
                CraftsNeeded = rootCrafts,
                TotalFocusNeeded = focusTotal,
                MonsterPrompts = prompts,
                Message = BridgeI18n.Format("craft_needs_monster_confirm", lang, ("desc", desc)),
            };
        }

        List<GoalStep> finalSteps = CoalesceSteps(steps);
        string rootName = JsonNodeHelpers.GetString(root, "name", "");
        return new CraftPlanResult
        {
            Status = "ready",
            TargetRecipe = root,
            RequestedCount = reqCnt,
            CraftsNeeded = rootCrafts,
            TotalFocusNeeded = focusTotal,
            Steps = finalSteps,
            Message = BridgeI18n.Format(
                "craft_ready",
                lang,
                ("name", rootName),
                ("crafts", rootCrafts),
                ("steps", finalSteps.Count)),
        };
    }

    private static List<GoalStep> CoalesceSteps(List<GoalStep> steps)
    {
        var merged = new List<GoalStep>();
        var gatherIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var fishIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var farmIdx = new Dictionary<(int ItemId, string Monster), int>();

        foreach (GoalStep st in steps)
        {
            if (st.ActionType == "gather_resource")
            {
                string res = JsonNodeHelpers.GetString(st.Params, "resource", "");
                if (gatherIdx.TryGetValue(res, out int idx))
                {
                    GoalStep prev = merged[idx];
                    prev.TargetCount += st.TargetCount;
                    prev.Params["count"] = prev.TargetCount;
                    continue;
                }
                gatherIdx[res] = merged.Count;
                merged.Add(st);
            }
            else if (st.ActionType == "fish")
            {
                string spot = JsonNodeHelpers.GetString(st.Params, "spot", "");
                if (fishIdx.TryGetValue(spot, out int idx))
                {
                    GoalStep prev = merged[idx];
                    prev.TargetCount += st.TargetCount;
                    prev.Params["count"] = prev.TargetCount;
                    continue;
                }
                fishIdx[spot] = merged.Count;
                merged.Add(st);
            }
            else if (st.ActionType == "farm_monster_item")
            {
                var key = (JsonNodeHelpers.GetInt(st.Params, "item_id", 0), JsonNodeHelpers.GetString(st.Params, "monster", ""));
                if (farmIdx.TryGetValue(key, out int idx))
                {
                    GoalStep prev = merged[idx];
                    prev.TargetCount = Math.Max(prev.TargetCount, st.TargetCount);
                    prev.Params["count"] = prev.TargetCount;
                    prev.Params["target_count"] = prev.TargetCount;
                    continue;
                }
                farmIdx[key] = merged.Count;
                merged.Add(st);
            }
            else
            {
                merged.Add(st);
            }
        }

        return merged;
    }

    private static int CeilDiv(int numerator, int denominator)
    {
        if (numerator <= 0)
            return 0;
        return (numerator + denominator - 1) / denominator;
    }
}
