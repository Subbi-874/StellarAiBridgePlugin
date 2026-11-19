using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.AgyBridge.Adapters.External;

namespace Stellar.AgyBridge.Gathering;

public sealed class ResolvedGatherTarget
{
    public int CollectionId { get; init; }
    public string Name { get; init; } = "";
    public int SceneId { get; init; }
    public string SceneName { get; init; } = "";
    public Position3D ClusterCenter { get; init; }
    public int ClusterNodeCount { get; init; }
    public bool HasClusterCoords { get; init; }
}

/// <summary>
/// Bridges Stellar.AgyBridge to the installed Stellar.AutoGather plugin via <see cref="IExternalPluginAdapter"/>,
/// allowing target resource locking, Focus mode toggling, exact completion tracking,
/// and lookup of gatherable clusters.
/// </summary>
public sealed class AutoGatherIntegration
{
    private readonly IExternalPluginAdapter _external;

    public AutoGatherIntegration(IExternalPluginAdapter external)
    {
        _external = external;
    }

    public bool IsAvailable => _external.EnsureAutoGatherResolved();

    public bool EnsureResolved() => _external.EnsureAutoGatherResolved();

    public bool IsGatheringEnabled() => _external.IsAutoGatherEnabled();

    public string GetControllerStatus() => _external.GetAutoGatherStatus();

    public string LastCompletionVia => _external.LastGatherCompletionVia;

    /// <summary>
    /// Monotonic count of nodes AutoGather has finished since the hook was installed (callers use deltas).
    /// Returns -1 when the hook is unavailable, so callers can fail safe instead of gathering forever.
    /// </summary>
    public long ReadCollectCounter() => _external.ReadGatherCompletedCount();

    public bool StartAutoGather(int configId, string lockedName, bool? useFocus = null)
        => _external.StartAutoGather(configId, lockedName, useFocus);

    public void StopAutoGather() => _external.StopAutoGather();

    /// <summary>
    /// Resolves a Korean/partial resource name or numeric CollectionId against LifeCollectListTableMgr,
    /// CollectionTableMgr, and CollectionEnrichmentTableMgr to find both the ConfigId and best 3D cluster coordinates.
    /// </summary>
    public ResolvedGatherTarget? ResolveResourceTarget(string resourceQuery, int localSceneId, Position3D localPos)
    {
        if (!_external.IsLuaReady || string.IsNullOrWhiteSpace(resourceQuery))
            return null;

        string qTrim = resourceQuery.Trim();
        if (qTrim.Equals("any", StringComparison.OrdinalIgnoreCase)
            || qTrim.Equals("all", StringComparison.OrdinalIgnoreCase)
            || qTrim.Equals("주변", StringComparison.OrdinalIgnoreCase)
            || qTrim.Equals("아무거나", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedGatherTarget
            {
                CollectionId = 0,
                Name = "주변 모든 채집물",
                SceneId = localSceneId,
                SceneName = "",
                ClusterCenter = localPos,
                ClusterNodeCount = 0,
                HasClusterCoords = false
            };
        }

        var obj = _external.QueryGatherResourceTarget(qTrim, localSceneId, localPos);
        if (obj == null)
            return null;

        return new ResolvedGatherTarget
        {
            CollectionId = obj["id"]?.GetValue<int>() ?? 0,
            Name = obj["name"]?.GetValue<string>() ?? qTrim,
            SceneId = obj["sceneId"]?.GetValue<int>() ?? localSceneId,
            SceneName = obj["sceneName"]?.GetValue<string>() ?? "",
            HasClusterCoords = obj["hasCluster"]?.GetValue<bool>() == true,
            ClusterCenter = new Position3D(
                obj["x"]?.GetValue<float>() ?? 0f,
                obj["y"]?.GetValue<float>() ?? 0f,
                obj["z"]?.GetValue<float>() ?? 0f),
            ClusterNodeCount = obj["count"]?.GetValue<int>() ?? 0
        };
    }

    public JsonObject ListGatherableResources(string? filter = null)
    {
        var res = _external.QueryGatherableResources(filter ?? "");
        if (res["ok"]?.GetValue<bool>() == true)
            res["autogather_available"] = IsAvailable;
        return res;
    }
}
