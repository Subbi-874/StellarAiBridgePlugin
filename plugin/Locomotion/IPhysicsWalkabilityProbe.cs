using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Read-only 3D world geometry walkability probe used by pathfinding modules
/// so real-time navigation and standalone verification share identical contracts.
/// </summary>
public interface IPhysicsWalkabilityProbe
{
    bool IsReady { get; }

    bool TrySampleGroundHeight(
        float x,
        float refY,
        float z,
        out float groundY,
        float upOffset = 1.35f,
        float downDepth = 2.10f,
        float radius = 0.22f);

    bool IsBlocked(Vector3 from, Vector3 to, float radius, bool ignoreStep = true);

    bool CanStep3D(Vector3 fromFloor, Vector3 toFloor, float bodyRadius = 0.36f);

    bool TrySampleSafeDropGround(
        float x,
        float refY,
        float z,
        float maxDropDepth,
        out float groundY,
        float minDropDepth = 0.85f,
        float radius = 0.24f);

    bool CanDropOff3D(Vector3 fromFloor, Vector3 toFloor, float bodyRadius, out bool requiresVault);
}
