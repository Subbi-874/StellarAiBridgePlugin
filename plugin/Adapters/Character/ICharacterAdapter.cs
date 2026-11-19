using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Adapters.Character;

/// <summary>
/// Domain adapter encapsulating character customization, mount, profession loadout metadata,
/// and monster spawn-table queries.
/// </summary>
public interface ICharacterAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }
    bool IsRiding();
    JsonObject Dismount();
    JsonObject StartMountCoroutine(string requestedType);
    Dictionary<int, string> ReadProfessionNames();
    bool ApplyFashionHiddenRegions(IReadOnlyDictionary<int, bool> hiddenRegions);
    IReadOnlyList<MonsterMatch> FindMonsters(string query, int maxMatches = 40);
    IReadOnlyList<Vector3> GetMonsterSpawnsInScene(int sceneId, IEnumerable<int> monsterIds);
    IReadOnlyList<int> FindMonsterCandidateScenes(IEnumerable<int> monsterIds, int excludeSceneId);
}
