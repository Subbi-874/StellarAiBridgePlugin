using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stellar.AgyBridge.World;

public enum QuestGoalKind
{
    Unknown = 0,
    Talk = 1,
    AutoFlowOrCutscene = 2,
    ReachArea = 3,
    InteractOperate = 4,
    KillMonster = 5,
    FollowNpc = 6,
    SubmitOrShowItem = 7,
    DoExpression = 8,
    Unsupported = 9
}

public sealed record QuestGoalInfo(
    int Index,
    int RawGoalType,
    QuestGoalKind Kind,
    int CurCount,
    int TargetCount,
    int SceneId,
    long Uid,
    int PosType,
    bool HasPosition,
    float X,
    float Y,
    float Z,
    string Param4,
    string Param5,
    bool IsCompleted,
    string Description);

public sealed record QuestEntry(
    int QuestId,
    string Name,
    int State,
    int QuestType,
    int GroupId,
    string GroupName,
    int GroupDisplayOrder,
    int QuestOrder,
    int UiRank,
    bool IsTracking,
    int StepId,
    string StepTips,
    bool DisableTransport,
    IReadOnlyList<QuestGoalInfo> Goals);

public sealed record QuestSelectionResult(
    IReadOnlyList<QuestEntry> Eligible,
    IReadOnlyList<(QuestEntry Quest, string Reason)> Excluded);

/// <summary>
/// Pure domain logic for Quest categorization, GoalType classification, UI top-to-bottom ordering,
/// and sequential eligibility filtering (zero Unity/Lua dependencies so unit tests run directly).
/// </summary>
public static class QuestRules
{
    public const int StateCanAccept = 1;
    public const int StateInProgress = 2;
    public const int StateDeliverable = 3;
    public const int StateEnd = 4;
    public const int StateNotEnough = 5;

    public const int GroupAll = 0;
    public const int GroupStory = 1;
    public const int GroupSide = 2;
    public const int GroupArea = 3;
    public const int GroupGuide = 4;
    public const int GroupEvent = 5;
    public const int GroupBond = 6;

    // Zproto.EEntityType integer values used by ZEntity.LuaEntType / EntType
    public const int EntNpc = 2;
    public const int EntSceneObject = 3;
    public const int EntZone = 5;
    public const int EntDrop = 12;
    public const int EntTrap = 15;
    public const int EntCollection = 16;
    public const int EntStaticObject = 18;

    /// <summary>
    /// Normalizes natural-language Korean/English/Japanese quest category names into game QuestTypeGroupIDs.
    /// Returns 0 for empty/"전체"/"all", positive group ID (1..6) when matched, or -1 if unknown non-empty input.
    /// </summary>
    public static int NormalizeCategoryGroupId(string? raw)
    {
        string norm = NormalizeText(raw);
        if (string.IsNullOrEmpty(norm)
            || norm is "전체" or "모두" or "전부" or "아무거나" or "all" or "any" or "auto" or "全て" or "すべて"
            || norm.StartsWith("전체퀘", StringComparison.Ordinal)
            || norm.StartsWith("모든퀘", StringComparison.Ordinal))
        {
            return GroupAll;
        }

        if (norm is "구역" or "지역" or "월드" or "area" or "region" or "zone" or "world" or "エリア" or "地域"
            || norm.Contains("구역퀘", StringComparison.Ordinal)
            || norm.Contains("지역퀘", StringComparison.Ordinal)
            || norm.Contains("월드퀘", StringComparison.Ordinal)
            || norm.Contains("areaquest", StringComparison.Ordinal))
        {
            return GroupArea;
        }

        if (norm is "스토리" or "메인" or "story" or "main" or "ストーリー" or "メイン"
            || norm.Contains("스토리퀘", StringComparison.Ordinal)
            || norm.Contains("메인퀘", StringComparison.Ordinal)
            || norm.Contains("mainquest", StringComparison.Ordinal)
            || norm.Contains("storyquest", StringComparison.Ordinal))
        {
            return GroupStory;
        }

        if (norm is "후일담" or "서브" or "에필로그" or "sub" or "side" or "epilogue" or "サブ" or "後日談"
            || norm.Contains("후일담", StringComparison.Ordinal)
            || norm.Contains("서브퀘", StringComparison.Ordinal)
            || norm.Contains("sidequest", StringComparison.Ordinal))
        {
            return GroupSide;
        }

        if (norm is "가이드" or "튜토리얼" or "입문" or "guide" or "tutorial" or "ガイド" or "チュートリアル"
            || norm.Contains("가이드퀘", StringComparison.Ordinal)
            || norm.Contains("guidequest", StringComparison.Ordinal))
        {
            return GroupGuide;
        }

        if (norm is "인연" or "호감" or "동료" or "bond" or "affinity" or "relationship" or "絆" or "親愛"
            || norm.Contains("인연스토리", StringComparison.Ordinal)
            || norm.Contains("인연퀘", StringComparison.Ordinal)
            || norm.Contains("bondstory", StringComparison.Ordinal))
        {
            return GroupBond;
        }

        if (norm is "이벤트" or "event" or "limited" or "イベント"
            || norm.Contains("이벤트퀘", StringComparison.Ordinal)
            || norm.Contains("eventquest", StringComparison.Ordinal))
        {
            return GroupEvent;
        }

        return -1;
    }

    public static string GetCanonicalCategoryName(int groupId) => groupId switch
    {
        GroupAll => "전체",
        GroupStory => "스토리",
        GroupSide => "후일담",
        GroupArea => "구역 퀘스트",
        GroupGuide => "가이드 퀘스트",
        GroupEvent => "이벤트 퀘스트",
        GroupBond => "인연 스토리",
        _ => "기타 퀘스트"
    };

    public static QuestGoalKind ClassifyGoalType(
        int rawGoalType,
        int posType = 0,
        string? description = null,
        string? stepTips = null)
    {
        switch (rawGoalType)
        {
            case 21:  // AutoLuaTalk
            case 22:  // NpcLuaTalk
            case 310: // NpcFlowTalk
                return QuestGoalKind.Talk;

            case 25:  // Server scene notification / auto transition
            case 200: // ServerPlayCutscene
            case 400: // AutoPlayCutscene
            case 409: // AutoPlayFlow
            case 412: // EpisodeStart
            case 413: // EpisodeEnd
                return QuestGoalKind.AutoFlowOrCutscene;

            case 23:  // TargetLuaArea
            case 208: // Zone trigger / Reach area (verified on active quests)
            case 334: // InMapArea
                return QuestGoalKind.ReachArea;

            case 29:  // Area search / object collect count
                return posType == 5 ? QuestGoalKind.InteractOperate : QuestGoalKind.ReachArea;

            case 302: // FinishOperate
                return QuestGoalKind.InteractOperate;

            case 9:   // KillMonster
            case 109: // Kill Monster group variant (e.g., quest #30121, #30658)
                return QuestGoalKind.KillMonster;

            case 24:  // Server-scripted defeat / investigation / interaction goal (e.g., #186621)
            case 137: // Server-scripted ambush / defeat step (e.g., #186621 step 006)
            case 340: // Ambush / Wave monster clear (e.g., quest #186022, #186027)
            {
                if (posType == 2)
                    return QuestGoalKind.Talk;
                if (posType == 5)
                    return QuestGoalKind.InteractOperate;

                bool hasSearchText = IsCollectOrSearchDescription(description) || IsCollectOrSearchDescription(stepTips);
                bool hasCombatText = IsCombatDescription(description) || IsCombatDescription(stepTips);
                if (posType == 4 && hasSearchText && !hasCombatText)
                    return QuestGoalKind.ReachArea;

                return QuestGoalKind.KillMonster;
            }

            case 303: // PlayerFollowNpc
            case 304: // PlayerFollowNpcWalk
            case 305: // NpcFollowPlayer
                return QuestGoalKind.FollowNpc;

            case 330: // SubmitItem
            case 331: // ShowItem
                return QuestGoalKind.SubmitOrShowItem;

            case 332: // DoExpression
                return QuestGoalKind.DoExpression;

            case 28:  // Time / prerequisite lock (NotEnough)
            case 106: // TargetTime
            case 320: // OpenInsight
            case 321: // TakePhoto
            case 322: // TargetNpcPhoto
            case 326: // TargetPhotoByTableId
            case 333: // TargetEntityPhoto
            case 343: // CameraPatternType
            case 346: // TargetMultiPlayerPhoto
            case 347: // TargetShortcutKeySetting
            case 405: // AutoOpenUI
                return QuestGoalKind.Unsupported;
        }

        if (rawGoalType <= 0)
            return QuestGoalKind.Unknown;

        // Fallback inference from E.GoalPosType when encounter unknown goal type IDs
        return posType switch
        {
            2 => QuestGoalKind.Talk,            // Npc
            3 => QuestGoalKind.KillMonster,     // Monster
            4 => QuestGoalKind.ReachArea,       // Zone
            5 => QuestGoalKind.InteractOperate, // SceneObject
            _ => QuestGoalKind.Unsupported
        };
    }

    public static bool IsGoalSupported(QuestGoalKind kind)
        => kind != QuestGoalKind.Unsupported && kind != QuestGoalKind.Unknown;

    public static QuestGoalInfo? GetActiveGoal(QuestEntry? entry)
    {
        if (entry == null || entry.Goals.Count == 0)
            return null;

        foreach (var g in entry.Goals)
        {
            if (!g.IsCompleted)
                return g;
        }

        return entry.Goals[0];
    }

    public static string DescribeUnsupportedReason(QuestGoalInfo? goal, int questState, string? stepTips)
    {
        if (questState == StateNotEnough)
            return !string.IsNullOrWhiteSpace(stepTips) ? stepTips! : "선행 조건 또는 시간 대기 중이라 진행할 수 없어요";

        if (questState == StateEnd)
            return "이미 완료된 퀘스트예요";

        if (goal == null)
            return "진행 가능한 퀘스트 목표가 없어요";

        return goal.RawGoalType switch
        {
            320 => "통찰(퍼즐/수사) 단계는 자동 진행을 지원하지 않아요",
            321 or 322 or 326 or 333 or 343 or 346 => "사진 촬영 단계는 자동 진행을 지원하지 않아요",
            106 or 28 => !string.IsNullOrWhiteSpace(stepTips) ? stepTips! : "게임 내 특정 시간/조건 대기 단계예요",
            347 or 405 => "UI/단축키 조작 단계는 자동 진행을 지원하지 않아요",
            _ => $"미지원 목표 유형(#{goal.RawGoalType}, kind={goal.Kind})"
        };
    }

    public static QuestSelectionResult SelectQuests(
        IReadOnlyList<QuestEntry> all,
        string? category = null,
        string? questQuery = null,
        int explicitQuestId = 0,
        IReadOnlySet<int>? skippedQuestIds = null)
    {
        var eligible = new List<QuestEntry>();
        var excluded = new List<(QuestEntry Quest, string Reason)>();

        string normQuery = NormalizeText(questQuery);
        int groupId = explicitQuestId > 0 ? GroupAll : NormalizeCategoryGroupId(category);
        string normCat = NormalizeText(category);

        var candidates = new List<QuestEntry>(all.Count);
        foreach (var q in all)
        {
            if (explicitQuestId > 0)
            {
                if (q.QuestId == explicitQuestId)
                    candidates.Add(q);
                continue;
            }

            if (!string.IsNullOrEmpty(normCat) && groupId != GroupAll)
            {
                if (groupId > 0)
                {
                    bool matchesGroup = q.GroupId == groupId
                        || NormalizeText(q.GroupName).Contains(NormalizeText(GetCanonicalCategoryName(groupId)), StringComparison.Ordinal);
                    if (!matchesGroup)
                        continue;
                }
                else
                {
                    if (!NormalizeText(q.GroupName).Contains(normCat, StringComparison.Ordinal))
                        continue;
                }
            }

            if (!string.IsNullOrEmpty(normQuery))
            {
                if (!NormalizeText(q.Name).Contains(normQuery, StringComparison.Ordinal))
                    continue;
            }

            candidates.Add(q);
        }

        // Exact quest-name match takes priority if user typed an exact name that is also a prefix of others
        if (!string.IsNullOrEmpty(normQuery) && candidates.Count > 1)
        {
            var exact = candidates.Where(q => string.Equals(NormalizeText(q.Name), normQuery, StringComparison.Ordinal)).ToList();
            if (exact.Count > 0)
                candidates = exact;
        }

        // Deterministic top-to-bottom UI order
        candidates.Sort((a, b) =>
        {
            int da = a.GroupDisplayOrder > 0 ? a.GroupDisplayOrder : 999;
            int db = b.GroupDisplayOrder > 0 ? b.GroupDisplayOrder : 999;
            int c = da.CompareTo(db);
            if (c != 0) return c;

            int ra = a.UiRank > 0 ? a.UiRank : int.MaxValue;
            int rb = b.UiRank > 0 ? b.UiRank : int.MaxValue;
            c = ra.CompareTo(rb);
            if (c != 0) return c;

            c = a.QuestOrder.CompareTo(b.QuestOrder);
            if (c != 0) return c;

            return a.QuestId.CompareTo(b.QuestId);
        });

        foreach (var q in candidates)
        {
            if (q.State == StateEnd)
            {
                excluded.Add((q, "이미 완료됨"));
                continue;
            }
            if (q.State == StateNotEnough)
            {
                excluded.Add((q, DescribeUnsupportedReason(GetActiveGoal(q), q.State, q.StepTips)));
                continue;
            }
            if (skippedQuestIds != null && skippedQuestIds.Contains(q.QuestId))
            {
                excluded.Add((q, "이번 자동 진행에서 건너뜀"));
                continue;
            }

            eligible.Add(q);
        }

        return new QuestSelectionResult(eligible, excluded);
    }

    /// <summary>
    /// Returns true if a monster entity is considered alive (allows unseeded newly-spawned entities with HasHpObservation == false).
    /// </summary>
    public static bool IsAliveMonsterCandidate(bool hasHpObservation, long hp)
        => !(hasHpObservation && hp <= 0L);

    /// <summary>
    /// Scores how closely a monster matches a quest's kill goal (exact config ID &gt; text match in goal/tips &gt; generic monster).
    /// </summary>
    public static int GetTargetMonsterMatchScore(
        int targetMonsterId,
        string? goalDesc,
        string? stepTips,
        int candidateMonsterId,
        string? candidateName)
    {
        if (targetMonsterId > 0 && candidateMonsterId == targetMonsterId)
            return 1000;

        string normName = NormalizeText(candidateName);
        if (normName.Length >= 2)
        {
            if (NormalizeText(goalDesc).Contains(normName, StringComparison.Ordinal)
                || NormalizeText(stepTips).Contains(normName, StringComparison.Ordinal))
            {
                return 500;
            }
        }

        return 0;
    }

    private static readonly string[] CombatKeywords =
    {
        "처치", "소탕", "물리치", "쓰러뜨", "격퇴", "토벌", "구출", "기습", "깡패", "몬스터",
        "defeat", "kill", "eliminate", "rescue", "討伐", "撃破", "倒す"
    };

    /// <summary>
    /// Returns true if a step/goal description indicates combat or enemy clearance (e.g. ReachArea rescue/ambush steps).
    /// </summary>
    public static bool IsCombatDescription(string? text)
    {
        string norm = NormalizeText(text);
        if (norm.Length == 0)
            return false;
        foreach (string kw in CombatKeywords)
        {
            if (norm.Contains(kw, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static readonly string[] CollectOrSearchKeywords =
    {
        "수집", "채집", "채취", "단서", "찾기", "찾아", "조사", "트랩", "해제", "해체", "줍기",
        "collect", "gather", "clue", "investigate", "search",
        "採集", "収集", "調べ"
    };

    /// <summary>
    /// Returns true when a step or goal text describes collecting/searching/interacting with
    /// points inside an area (e.g. glowing pillars, traps, clues).
    /// </summary>
    public static bool IsCollectOrSearchDescription(string? text)
    {
        string norm = NormalizeText(text);
        if (norm.Length == 0)
            return false;
        foreach (string kw in CollectOrSearchKeywords)
        {
            if (norm.Contains(kw, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public const float CombatTriggerArrivalHorizMeters = 2.2f;
    public const float CombatTriggerArrivalVertMeters = 2.5f;

    /// <summary>
    /// Decides whether a <see cref="QuestGoalKind.ReachArea"/> goal should route to combat.
    /// When an active quest interaction prompt is ready and no live quest monster remains nearby,
    /// interaction takes priority over stale combat wording in step tips.
    /// When no live monster is present, combat routing waits until the player reaches the trigger center.
    /// </summary>
    public static bool ShouldRouteReachAreaToCombat(
        bool isCombatDesc,
        bool inInstanceScene,
        bool hasLiveNearbyMonster,
        bool hasMatchingInteract,
        bool isNearGoalCenter = true)
    {
        if (hasMatchingInteract && !hasLiveNearbyMonster)
            return false;

        if (hasLiveNearbyMonster)
            return isCombatDesc || inInstanceScene;

        return isCombatDesc && isNearGoalCenter;
    }

    /// <summary>
    /// Decides whether a combat goal with zero currently spawned quest monsters should keep walking
    /// into the target marker center (to trigger ambush/proximity spawns) instead of idling within 13m.
    /// </summary>
    public static bool ShouldApproachCombatTrigger(
        bool hasGoalPosition,
        float horizDist,
        float absDy,
        bool hasLiveMonster,
        bool isActivelyFighting)
    {
        if (!hasGoalPosition || hasLiveMonster || isActivelyFighting)
            return false;

        return horizDist > CombatTriggerArrivalHorizMeters
            || absDy > CombatTriggerArrivalVertMeters;
    }

    /// <summary>
    /// Computes priority (&gt; 0 for valid interactable/area quest object candidates, 0 otherwise)
    /// from <c>Zproto.EEntityType</c> integer IDs and whether the entity was created by the active step.
    /// </summary>
    public static int GetQuestObjectEntityPriority(int entType, bool isStepCreated)
    {
        int basePriority = entType switch
        {
            EntCollection => 50,
            EntSceneObject => 45,
            EntDrop => 45,
            EntTrap => 40,
            EntStaticObject => 35,
            EntNpc => isStepCreated ? 30 : 0,
            EntZone => 10,
            _ => 0
        };

        if (basePriority <= 0)
            return 0;

        return isStepCreated ? basePriority + 100 : basePriority;
    }

    /// <summary>
    /// Returns true when the active goal has no target coordinates or scene (waiting on a spawned
    /// HUD sub-quest) and its description matches the currently tracked HUD quest's name.
    /// </summary>
    public static bool IsSubQuestHandoffGoal(QuestGoalInfo? activeGoal, string? hudTrackedQuestName)
    {
        if (activeGoal is not { HasPosition: false, SceneId: <= 0 })
            return false;

        string normHudName = NormalizeText(hudTrackedQuestName);
        if (normHudName.Length == 0)
            return false;

        string normDesc = NormalizeText(activeGoal.Description);
        return normDesc.Length > 0 && normDesc.Contains(normHudName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether AutoQuest should dynamically switch its active quest to the game HUD's
    /// tracked quest (e.g. when entering a quest instance spawns a local tracked sub-quest).
    /// </summary>
    public static bool ShouldFollowHudTrackedQuest(
        int activeQuestId,
        int hudTrackingQuestId,
        bool inInstanceScene,
        bool isSingleQuestTarget,
        bool trackedIsEligibleInCategory,
        bool trackedExistsAndInProgress,
        bool activeWaitingOnSubQuest = false)
    {
        if (hudTrackingQuestId <= 0
            || hudTrackingQuestId == activeQuestId
            || (isSingleQuestTarget && !activeWaitingOnSubQuest)
            || !trackedExistsAndInProgress)
        {
            return false;
        }

        return inInstanceScene || trackedIsEligibleInCategory || activeWaitingOnSubQuest;
    }

    public static int GetQuestSceneId(QuestEntry? entry)
    {
        if (entry == null || entry.Goals.Count == 0)
            return 0;

        int activeScene = GetActiveGoal(entry)?.SceneId ?? 0;
        if (activeScene > 0)
            return activeScene;

        for (int i = 0; i < entry.Goals.Count; i++)
        {
            if (entry.Goals[i].SceneId > 0)
                return entry.Goals[i].SceneId;
        }
        return 0;
    }

    private static readonly string[] RegionTrailingSuffixes =
    {
        "구역퀘스트", "지역퀘스트", "월드퀘스트", "서브퀘스트", "스토리퀘스트",
        "구역퀘", "지역퀘", "퀘스트", "지역", "구역", "맵", "쪽", "부근", "근처", "일대", "에서", "의"
    };

    private static string StripRegionSuffix(string normRegion)
    {
        string cur = normRegion;
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (string suffix in RegionTrailingSuffixes)
            {
                if (cur.EndsWith(suffix, StringComparison.Ordinal) && cur.Length - suffix.Length >= 2)
                {
                    cur = cur[..^suffix.Length];
                    stripped = true;
                    break;
                }
            }
        }
        return cur;
    }

    public readonly record struct ResolvedAutoQuestFilters(
        string Category,
        string Query,
        string Region,
        bool RegionOnly);

    /// <summary>
    /// Normalizes LLM-provided auto_quest/list_quests parameters so region names accidentally placed
    /// inside <paramref name="query"/> or <paramref name="category"/> (or omitted by remote agents
    /// without our system prompt, leaving them only in <paramref name="triggerText"/>) are promoted
    /// to <c>Region</c> dynamically against live scene names present in <paramref name="all"/>.
    /// </summary>
    public static ResolvedAutoQuestFilters ResolveAutoQuestFilters(
        IReadOnlyList<QuestEntry> all,
        string? category,
        string? query,
        string? region,
        bool regionOnly,
        int explicitQuestId,
        int currentSceneId,
        Func<int, string>? resolveSceneName,
        string? triggerText = null)
    {
        string cat = (category ?? "").Trim();
        string q = (query ?? "").Trim();
        string reg = (region ?? "").Trim();
        bool regOnly = regionOnly;

        if (explicitQuestId > 0 || all.Count == 0)
            return new ResolvedAutoQuestFilters(cat, q, reg, regOnly);

        // Collect distinct live scene names from active quests, longest normalized name first
        var sceneNameCandidates = new List<(string Norm, string Raw)>();
        var seenNormScenes = new HashSet<string>(StringComparer.Ordinal);
        if (resolveSceneName != null)
        {
            for (int i = 0; i < all.Count; i++)
            {
                int sid = GetQuestSceneId(all[i]);
                if (sid <= 0) continue;
                string rawScene = (resolveSceneName(sid) ?? "").Trim();
                string normScene = NormalizeText(rawScene);
                if (normScene.Length >= 2 && seenNormScenes.Add(normScene))
                    sceneNameCandidates.Add((normScene, rawScene));
            }
            sceneNameCandidates.Sort((a, b) => b.Norm.Length.CompareTo(a.Norm.Length));
        }

        string? TryFindSceneMention(string? rawText, bool allowCurrentAlias)
        {
            string norm = NormalizeText(rawText);
            if (norm.Length < 2)
                return null;

            foreach (var (normScene, rawScene) in sceneNameCandidates)
            {
                if (norm.Contains(normScene, StringComparison.Ordinal))
                    return rawScene;
            }

            if (allowCurrentAlias
                && currentSceneId > 0
                && (norm.Contains("현재지역", StringComparison.Ordinal)
                    || norm.Contains("현재맵", StringComparison.Ordinal)
                    || norm.Contains("이지역", StringComparison.Ordinal)
                    || norm.Contains("이맵", StringComparison.Ordinal)))
            {
                return "현재 지역";
            }

            return null;
        }

        string InferCategoryFromCompoundText(string? rawText)
        {
            string norm = NormalizeText(rawText);
            if (norm.Length == 0)
                return "";
            if (norm.Contains("구역퀘", StringComparison.Ordinal) || norm.Contains("지역퀘", StringComparison.Ordinal) || norm.Contains("월드퀘", StringComparison.Ordinal))
                return GetCanonicalCategoryName(GroupArea);
            if (norm.Contains("스토리", StringComparison.Ordinal) || norm.Contains("메인퀘", StringComparison.Ordinal))
                return GetCanonicalCategoryName(GroupStory);
            if (norm.Contains("후일담", StringComparison.Ordinal) || norm.Contains("서브퀘", StringComparison.Ordinal))
                return GetCanonicalCategoryName(GroupSide);
            if (norm.Contains("가이드퀘", StringComparison.Ordinal) || norm.Contains("튜토리얼", StringComparison.Ordinal))
                return GetCanonicalCategoryName(GroupGuide);
            if (norm.Contains("인연스토리", StringComparison.Ordinal) || norm.Contains("인연퀘", StringComparison.Ordinal))
                return GetCanonicalCategoryName(GroupBond);
            if (norm.Contains("이벤트퀘", StringComparison.Ordinal))
                return GetCanonicalCategoryName(GroupEvent);
            return "";
        }

        // 1. If region is empty and query matches no quest name, check if query is actually a region/compound phrase
        if (string.IsNullOrEmpty(reg) && !string.IsNullOrEmpty(q))
        {
            var probe = SelectQuests(all, cat, q, explicitQuestId: 0);
            if (probe.Eligible.Count == 0 && probe.Excluded.Count == 0)
            {
                string? mentionedScene = TryFindSceneMention(q, allowCurrentAlias: true);
                if (!string.IsNullOrEmpty(mentionedScene))
                {
                    reg = mentionedScene;
                    if (string.IsNullOrEmpty(cat))
                        cat = InferCategoryFromCompoundText(q);
                    q = "";
                }
                else if (CountMatchingRegionQuests(all, q, currentSceneId, resolveSceneName) > 0)
                {
                    reg = q;
                    if (string.IsNullOrEmpty(cat))
                        cat = InferCategoryFromCompoundText(q);
                    q = "";
                }
            }
        }

        // 2. If region is empty and category contains a region name (e.g. "몽테놀 계곡 구역 퀘스트" or "몽테놀 계곡")
        if (string.IsNullOrEmpty(reg) && !string.IsNullOrEmpty(cat))
        {
            string? mentionedInCat = TryFindSceneMention(cat, allowCurrentAlias: true);
            if (!string.IsNullOrEmpty(mentionedInCat))
            {
                reg = mentionedInCat;
                int gid = NormalizeCategoryGroupId(cat);
                cat = gid > 0 ? GetCanonicalCategoryName(gid) : "";
            }
            else if (NormalizeCategoryGroupId(cat) < 0 && CountMatchingRegionQuests(all, cat, currentSceneId, resolveSceneName) > 0)
            {
                reg = cat;
                cat = "";
            }
        }

        // 3. If region is still empty and no specific quest query was given, infer region mention from user's trigger text
        if (string.IsNullOrEmpty(reg) && string.IsNullOrEmpty(q) && !string.IsNullOrWhiteSpace(triggerText))
        {
            string? mentionedInTrig = TryFindSceneMention(triggerText, allowCurrentAlias: true);
            if (!string.IsNullOrEmpty(mentionedInTrig))
                reg = mentionedInTrig;
        }

        // 4. If region was given as a noisy phrase that doesn't match directly, extract canonical scene name from it
        if (!string.IsNullOrEmpty(reg) && CountMatchingRegionQuests(all, reg, currentSceneId, resolveSceneName) == 0)
        {
            string? extracted = TryFindSceneMention(reg, allowCurrentAlias: true);
            if (!string.IsNullOrEmpty(extracted))
                reg = extracted;
        }

        return new ResolvedAutoQuestFilters(cat, q, reg, regOnly);
    }

    /// <summary>
    /// Scores how closely a quest's target scene matches <paramref name="regionQuery"/>
    /// (2 = exact match / '현재 지역' / exact scene ID, 1 = substring/partial match, 0 = no match).
    /// </summary>
    public static int GetRegionMatchScore(
        QuestEntry? entry,
        string? regionQuery,
        int currentSceneId,
        Func<int, string>? resolveSceneName)
    {
        string normRegion = NormalizeText(regionQuery);
        if (normRegion.Length == 0)
            return 0;

        int qScene = GetQuestSceneId(entry);
        if (qScene <= 0)
            return 0;

        if (normRegion is "현재지역" or "현재맵" or "여기" or "이지역" or "이맵" or "현위치" or "current" or "here" or "currentregion" or "currentmap")
            return (currentSceneId > 0 && qScene == currentSceneId) ? 2 : 0;

        if (int.TryParse(normRegion, NumberStyles.Integer, CultureInfo.InvariantCulture, out int reqSceneId) && reqSceneId > 0)
            return qScene == reqSceneId ? 2 : 0;

        string sceneName = resolveSceneName?.Invoke(qScene) ?? "";
        string normScene = NormalizeText(sceneName);
        if (normScene.Length == 0)
            return 0;

        string effectiveRegion = StripRegionSuffix(normRegion);
        if (string.Equals(normScene, normRegion, StringComparison.Ordinal)
            || string.Equals(normScene, effectiveRegion, StringComparison.Ordinal))
        {
            return 2;
        }

        if (effectiveRegion.Length >= 2
            && (normScene.Contains(effectiveRegion, StringComparison.Ordinal)
                || effectiveRegion.Contains(normScene, StringComparison.Ordinal)))
        {
            return 1;
        }

        return 0;
    }

    public static int CountMatchingRegionQuests(
        IReadOnlyList<QuestEntry> quests,
        string? regionQuery,
        int currentSceneId,
        Func<int, string>? resolveSceneName,
        bool supportedOnly = false)
    {
        if (quests.Count == 0 || string.IsNullOrWhiteSpace(regionQuery))
            return 0;

        int count = 0;
        for (int i = 0; i < quests.Count; i++)
        {
            var q = quests[i];
            if (supportedOnly)
            {
                var g = GetActiveGoal(q);
                if (g == null || !IsGoalSupported(g.Kind))
                    continue;
            }
            if (GetRegionMatchScore(q, regionQuery, currentSceneId, resolveSceneName) > 0)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Reorders eligible quests so quests matching <paramref name="preferredRegion"/> (when specified)
    /// or quests actionable in the current scene/instance run first while preserving relative UI order.
    /// </summary>
    public static IReadOnlyList<QuestEntry> PreferCurrentContextQuests(
        IReadOnlyList<QuestEntry> eligible,
        int currentSceneId,
        int hudTrackingQuestId,
        bool inInstanceScene,
        string? preferredRegion = null,
        bool regionOnly = false,
        Func<int, string>? resolveSceneName = null)
    {
        if (eligible.Count == 0)
            return eligible;

        bool hasRegion = !string.IsNullOrWhiteSpace(preferredRegion);
        if (!hasRegion && eligible.Count <= 1)
            return eligible;

        if (hasRegion && !inInstanceScene)
        {
            var exactHud = new List<QuestEntry>();
            var exactOther = new List<QuestEntry>();
            var partialHud = new List<QuestEntry>();
            var partialOther = new List<QuestEntry>();
            var restHud = new List<QuestEntry>();
            var restLocal = new List<QuestEntry>();
            var restOther = new List<QuestEntry>();

            for (int i = 0; i < eligible.Count; i++)
            {
                var q = eligible[i];
                int score = GetRegionMatchScore(q, preferredRegion, currentSceneId, resolveSceneName);
                bool isHud = hudTrackingQuestId > 0 && q.QuestId == hudTrackingQuestId;
                int gScene = GetActiveGoal(q)?.SceneId ?? 0;
                bool isLocal = currentSceneId > 0 && gScene == currentSceneId;

                if (score >= 2)
                {
                    (isHud ? exactHud : exactOther).Add(q);
                }
                else if (score == 1)
                {
                    (isHud ? partialHud : partialOther).Add(q);
                }
                else if (!regionOnly)
                {
                    if (isHud && (currentSceneId <= 0 || gScene <= 0 || gScene == currentSceneId))
                        restHud.Add(q);
                    else if (isLocal)
                        restLocal.Add(q);
                    else
                        restOther.Add(q);
                }
            }

            var regionOrdered = new List<QuestEntry>(eligible.Count);
            regionOrdered.AddRange(exactHud);
            regionOrdered.AddRange(exactOther);
            regionOrdered.AddRange(partialHud);
            regionOrdered.AddRange(partialOther);
            if (!regionOnly)
            {
                regionOrdered.AddRange(restHud);
                regionOrdered.AddRange(restLocal);
                regionOrdered.AddRange(restOther);
            }
            return regionOrdered;
        }

        if (hasRegion && inInstanceScene)
        {
            var instHud = new List<QuestEntry>();
            var instRegion = new List<QuestEntry>();
            var instOther = new List<QuestEntry>();
            var remoteRegion = new List<QuestEntry>();
            var remoteOther = new List<QuestEntry>();

            for (int i = 0; i < eligible.Count; i++)
            {
                var q = eligible[i];
                int score = GetRegionMatchScore(q, preferredRegion, currentSceneId, resolveSceneName);
                int gScene = GetActiveGoal(q)?.SceneId ?? 0;
                bool isInstQuest = gScene <= 0 || currentSceneId <= 0 || gScene == currentSceneId;
                bool isHud = hudTrackingQuestId > 0 && q.QuestId == hudTrackingQuestId;

                if (isInstQuest)
                {
                    if (isHud)
                        instHud.Add(q);
                    else if (score > 0)
                        instRegion.Add(q);
                    else
                        instOther.Add(q);
                }
                else if (!regionOnly)
                {
                    if (score > 0)
                        remoteRegion.Add(q);
                    else
                        remoteOther.Add(q);
                }
            }

            var instOrdered = new List<QuestEntry>(eligible.Count);
            instOrdered.AddRange(instHud);
            instOrdered.AddRange(instRegion);
            instOrdered.AddRange(instOther);
            if (!regionOnly)
            {
                instOrdered.AddRange(remoteRegion);
                instOrdered.AddRange(remoteOther);
            }
            return instOrdered;
        }

        var tier0 = new List<QuestEntry>();
        var tier1 = new List<QuestEntry>();
        var tier2 = new List<QuestEntry>(eligible.Count);

        for (int i = 0; i < eligible.Count; i++)
        {
            var q = eligible[i];
            var g = GetActiveGoal(q);
            int gScene = g?.SceneId ?? 0;

            if (hudTrackingQuestId > 0
                && q.QuestId == hudTrackingQuestId
                && (inInstanceScene || currentSceneId <= 0 || gScene <= 0 || gScene == currentSceneId))
            {
                tier0.Add(q);
            }
            else if (currentSceneId > 0 && gScene == currentSceneId)
            {
                tier1.Add(q);
            }
            else
            {
                tier2.Add(q);
            }
        }

        var ordered = new List<QuestEntry>(eligible.Count);
        ordered.AddRange(tier0);
        ordered.AddRange(tier1);
        ordered.AddRange(tier2);
        return ordered;
    }

    public static string NormalizeText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int len = 0;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch) || ch is '·' or '・' or '-' or '_' or '[' or ']' or '(' or ')')
                continue;
            buf[len++] = char.ToLowerInvariant(ch);
        }
        return new string(buf[..len]);
    }

    /// <summary>
    /// Returns the retry cooldown after triggering an in-game quest interaction.
    /// Channeled gather/operate prompts need ~2.8s so re-triggering or walking does not cancel the cast bar.
    /// </summary>
    public static float ComputeQuestInteractRetryCooldown(QuestGoalKind goalKind, string? interactLabel)
    {
        if (goalKind == QuestGoalKind.InteractOperate)
            return 2.80f;

        string lbl = (interactLabel ?? "").Trim();
        if (lbl.Length > 0)
        {
            string low = lbl.ToLowerInvariant();
            if (lbl.Contains("채집", StringComparison.Ordinal)
                || lbl.Contains("채취", StringComparison.Ordinal)
                || lbl.Contains("수집", StringComparison.Ordinal)
                || lbl.Contains("조사", StringComparison.Ordinal)
                || lbl.Contains("해제", StringComparison.Ordinal)
                || lbl.Contains("해체", StringComparison.Ordinal)
                || lbl.Contains("수리", StringComparison.Ordinal)
                || lbl.Contains("작동", StringComparison.Ordinal)
                || lbl.Contains("조작", StringComparison.Ordinal)
                || low.Contains("gather", StringComparison.Ordinal)
                || low.Contains("collect", StringComparison.Ordinal)
                || low.Contains("investigate", StringComparison.Ordinal))
            {
                return 2.80f;
            }
        }

        return 1.20f;
    }

    /// <summary>
    /// Determines whether the character should remain stationary while waiting for a triggered quest interaction
    /// to complete, instead of walking closer and cancelling a channeled gather/operate cast bar.
    /// </summary>
    public static bool ShouldHoldStillForQuestInteract(
        bool hasMatchingInteract,
        bool huggingTarget,
        bool isInteractingPhase,
        int consecutiveTries,
        QuestGoalKind goalKind)
    {
        if (huggingTarget)
            return true;

        if (!isInteractingPhase)
            return false;

        if (hasMatchingInteract)
        {
            int maxHoldTries = goalKind == QuestGoalKind.InteractOperate ? 4 : 3;
            return consecutiveTries <= maxHoldTries;
        }

        return consecutiveTries < 2;
    }

    /// <summary>
    /// Prevents search-area sweep from walking over an active interactable object and prematurely marking it
    /// visited/blocked while a matching in-range prompt is actively being interacted with.
    /// </summary>
    public static bool ShouldDeferSearchAreaSweepForActiveInteract(
        bool hasMatchingInteract,
        bool isInteractingPhase,
        int consecutiveTries,
        QuestGoalKind goalKind)
        => hasMatchingInteract
           && (isInteractingPhase || goalKind == QuestGoalKind.InteractOperate)
           && consecutiveTries < 5;
}
