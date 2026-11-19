using System;
using System.Collections.Generic;

namespace Stellar.AgyBridge.Adapters;

public enum CapabilityState
{
    Unknown = 0,
    Available = 1,
    Degraded = 2,
    Missing = 3
}

public sealed record CapabilityEntry(
    string Key,
    string Label,
    string Domain,
    CapabilityState State,
    string Detail);

public interface ICapabilityProbe
{
    string Domain { get; }

    /// <summary>
    /// Runs once at plugin initialization. Implementations MUST NOT access game singletons,
    /// Unity scene instances, or Lua globals here — only inspect loaded CLR type/assembly metadata.
    /// </summary>
    IReadOnlyList<CapabilityEntry> ProbeLoadTime();

    /// <summary>
    /// Runs after the local player has safely entered the world and Lua is ready.
    /// Must perform strictly non-destructive existence checks (never invoke side-effecting RPCs).
    /// </summary>
    IReadOnlyList<CapabilityEntry> ProbeInWorld();
}

public static class CapabilityKeys
{
    public const string Revive = "revive";
    public const string Mount = "mount";
    public const string Party = "party";
    public const string Interaction = "interaction";
    public const string PlayerLocate = "player_locate";
    public const string Loadout = "loadout";
    public const string Outfit = "outfit";
    public const string MonsterSeek = "monster_seek";
    public const string Dungeon = "dungeon";
    public const string AutoReady = "auto_ready";
    public const string Emote = "emote";
    public const string Sticker = "sticker";
    public const string TeleportChannel = "teleport_channel";
    public const string TraversalGimmick = "traversal_gimmick";
    public const string AutoCombat = "auto_combat";
    public const string CombatEvade = "combat_evade";
    public const string CameraGuard = "camera_guard";
    public const string PhysicsNav = "physics_nav";
    public const string AutoGather = "auto_gather";
    public const string AutoFish = "auto_fish";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [Revive] = "부활",
        [Mount] = "탈것 탑승/하차",
        [Party] = "파티 관리",
        [Interaction] = "2인 상호작용/동승 승인·요청",
        [PlayerLocate] = "유저 위치 조회",
        [Loadout] = "직업·스킬 프리셋",
        [Outfit] = "의상 프리셋",
        [MonsterSeek] = "몬스터 찾아가기",
        [Dungeon] = "던전·레이드·불안정공간 입장/퇴장",
        [AutoReady] = "던전·레이드 자동 준비",
        [Emote] = "3D 감정표현",
        [Sticker] = "채팅 내장 이모티콘",
        [TeleportChannel] = "맵 포탈·채널 이동",
        [TraversalGimmick] = "이동형 필드·던전 기믹",
        [AutoCombat] = "자동전투",
        [CombatEvade] = "장판·보스 패턴 회피",
        [CameraGuard] = "시점 자동 복구",
        [PhysicsNav] = "실내 물리 길찾기(ZPhysics)",
        [AutoGather] = "자동 채집(AutoGather 연동)",
        [AutoFish] = "자동 낚시(AutoFishing 연동)"
    };

    public static string GetLabel(string key) => Labels.TryGetValue(key, out var label) ? label : key;
}
