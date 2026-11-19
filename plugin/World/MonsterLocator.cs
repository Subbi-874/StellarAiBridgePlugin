using System;
using System.Collections.Generic;
using Stellar.AgyBridge.Adapters.Character;
using UnityEngine;

namespace Stellar.AgyBridge.World;

public sealed record MonsterMatch(int Id, string Name, int MonsterType);

public sealed record MonsterSpawnSet(int SceneId, string SceneName, IReadOnlyList<Vector3> Points);

/// <summary>
/// Static-data monster lookups delegated through <see cref="ICharacterAdapter"/>.
/// </summary>
public sealed class MonsterLocator
{
    private const int MaxMatches = 40;

    private readonly ICharacterAdapter _character;
    private readonly WorldStateService _world;

    public MonsterLocator(ICharacterAdapter character, WorldStateService world)
    {
        _character = character;
        _world = world;
    }

    /// <summary>Finds monsters by numeric id or case-insensitive name (exact matches first, prioritizing MonsterHuntList field bosses/mobs).</summary>
    public IReadOnlyList<MonsterMatch> FindMonsters(string query)
        => _character.FindMonsters(query, MaxMatches);

    /// <summary>Spawn points of any of <paramref name="monsterIds"/> in one scene.</summary>
    public MonsterSpawnSet GetSpawns(int sceneId, IEnumerable<int> monsterIds)
        => new(sceneId, _world.GetSceneName(sceneId), _character.GetMonsterSpawnsInScene(sceneId, monsterIds));

    /// <summary>
    /// Other scenes that contain any of the monsters: monster-hunt-list scenes first, then every open-field scene
    /// (SceneSubType 4). Returns scene ids ordered by that priority.
    /// </summary>
    public IReadOnlyList<int> FindCandidateScenes(IEnumerable<int> monsterIds, int excludeSceneId)
        => _character.FindMonsterCandidateScenes(monsterIds, excludeSceneId);

    public static Vector3 Nearest(IReadOnlyList<Vector3> points, Vector3 from)
    {
        Vector3 best = points[0];
        float bestD = float.MaxValue;
        foreach (var p in points)
        {
            float dx = p.x - from.x, dz = p.z - from.z;
            float d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
