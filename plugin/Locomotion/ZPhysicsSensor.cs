using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Lazy main-thread wrapper around Panda.ZGame.ZPhysics (CheckCollideSphereCast)
/// with UnityEngine.Physics.Raycast fallback for 3D obstacle, ground-height, and cliff probing.
/// </summary>
public sealed class ZPhysicsSensor : IPhysicsWalkabilityProbe
{
    private static readonly object BoxTrue = true;
    private static readonly object BoxFalse = false;
    private static readonly float[] UnityFallbackFractions = { 0.25f, 0.50f, 0.75f, 1.0f };

    private readonly IPluginServices _services;

    private bool _resolved;
    private bool _ready;
    private bool _useUnityFallback;
    private bool _warnedError;

    // Panda.ZGame.ZPhysics reflection cache
    private MethodInfo? _miSphereCast;
    private int _blockMask;
    private int _groundOrBlockMask;
    private object _blockMaskBox = 0;
    private object _groundOrBlockMaskBox = 0;
    private object? _hitBox;
    private FieldInfo? _fiHitDistance;
    private FieldInfo? _fiHitPoint;
    private readonly object?[] _sphereArgs = new object?[7];

    // UnityEngine.Physics fallback reflection cache
    private MethodInfo? _miUnityRaycast;
    private readonly object?[] _rayArgs = new object?[4];

    public ZPhysicsSensor(IPluginServices services)
    {
        _services = services;
    }

    public bool IsReady => EnsureResolved();

    public bool EnsureResolved()
    {
        if (_resolved)
            return _ready;
        _resolved = true;

        try
        {
            if (TryResolveZPhysics())
            {
                _ready = true;
                _services.Log.Info($"[AgyBridge][Nav] ZPhysicsSensor bound to Panda.ZGame.ZPhysics (blockMask=0x{_blockMask:X}, groundMask=0x{_groundOrBlockMask:X})");
                return true;
            }

            if (TryResolveUnityPhysics())
            {
                _useUnityFallback = true;
                _ready = true;
                _services.Log.Info("[AgyBridge][Nav] ZPhysicsSensor fell back to UnityEngine.Physics.Raycast.");
                return true;
            }

            _services.Log.Info("[AgyBridge][Nav] ZPhysicsSensor unavailable; SmartPathNavigator will use fallback steering.");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Nav] ZPhysicsSensor resolve error: {ex.Message}");
        }

        return false;
    }

    private bool TryResolveZPhysics()
    {
        var zpType = InteropResolver.FindType("Panda.ZGame.ZPhysics");
        if (zpType == null)
            return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        int blockMask = ReadMaskProperty(zpType.GetProperty("BlockLayerMask", flags));
        int groundMask = ReadMaskProperty(zpType.GetProperty("GroundLayerMask", flags));

        MethodInfo? miSphere = null;
        foreach (var m in zpType.GetMethods(flags))
        {
            if (!string.Equals(m.Name, "CheckCollideSphereCast", StringComparison.Ordinal))
                continue;

            var ps = m.GetParameters();
            if (ps.Length == 7
                && ps[0].ParameterType == typeof(Vector3)
                && ps[1].ParameterType == typeof(float)
                && ps[2].ParameterType == typeof(Vector3)
                && ps[3].ParameterType == typeof(int)
                && ps[4].ParameterType.IsByRef
                && ps[5].ParameterType == typeof(bool)
                && ps[6].ParameterType == typeof(bool)
                && m.ReturnType == typeof(bool))
            {
                miSphere = m;
                break;
            }
        }

        if (miSphere == null)
            return false;

        Type? hitType = miSphere.GetParameters()[4].ParameterType.GetElementType();
        if (hitType == null)
            return false;

        _hitBox = Activator.CreateInstance(hitType);
        if (_hitBox == null)
            return false;

        var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _fiHitDistance = hitType.GetField("Distance", instFlags) ?? hitType.GetField("distance", instFlags);
        _fiHitPoint = hitType.GetField("Point", instFlags) ?? hitType.GetField("point", instFlags);

        if (blockMask == 0 && groundMask == 0)
            blockMask = -1;

        _miSphereCast = miSphere;
        _blockMask = blockMask;
        _groundOrBlockMask = (groundMask | blockMask) != 0 ? (groundMask | blockMask) : -1;
        _blockMaskBox = _blockMask;
        _groundOrBlockMaskBox = _groundOrBlockMask;
        return true;
    }

    private bool TryResolveUnityPhysics()
    {
        var physType = InteropResolver.FindType("UnityEngine.Physics");
        if (physType == null)
            return false;

        foreach (var m in physType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!string.Equals(m.Name, "Raycast", StringComparison.Ordinal))
                continue;
            var ps = m.GetParameters();
            if (ps.Length == 4
                && ps[0].ParameterType == typeof(Vector3)
                && ps[1].ParameterType == typeof(Vector3)
                && ps[2].ParameterType == typeof(float)
                && ps[3].ParameterType == typeof(int)
                && m.ReturnType == typeof(bool))
            {
                _miUnityRaycast = m;
                _blockMask = -1;
                _groundOrBlockMask = -1;
                _blockMaskBox = -1;
                _groundOrBlockMaskBox = -1;
                return true;
            }
        }

        return false;
    }

    private static int ReadMaskProperty(PropertyInfo? pi)
    {
        if (pi == null) return 0;
        try
        {
            object? val = pi.GetValue(null);
            return val is int mask ? mask : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Checks whether a sphere cast from <paramref name="from"/> to <paramref name="to"/>
    /// intersects world blocking colliders.
    /// </summary>
    public bool IsBlocked(Vector3 from, Vector3 to, float radius, bool ignoreStep = true)
    {
        Vector3 delta = to - from;
        if (delta.sqrMagnitude < 1e-5f)
            return false;

        if (!EnsureResolved())
            return false;

        return InvokeCast(from, radius, to, delta, _blockMaskBox, ignoreStep);
    }

    /// <summary>
    /// Casts along <paramref name="dir"/> up to <paramref name="maxDist"/> and returns the free
    /// travel distance before hitting a blocking collider (returns <paramref name="maxDist"/> when clear).
    /// </summary>
    public float ProbeClearDistance(Vector3 from, Vector3 dir, float maxDist, float radius, bool ignoreStep = true)
    {
        if (maxDist <= 1e-4f || dir.sqrMagnitude < 1e-6f)
            return maxDist;

        dir = dir.normalized;
        if (!EnsureResolved())
            return maxDist;

        try
        {
            if (!_useUnityFallback && _miSphereCast != null)
            {
                Vector3 to = from + dir * maxDist;
                if (!TryRawSphereCast(from, radius, to, _blockMaskBox, ignoreStep, out float hitDist, out Vector3 hitPoint))
                    return maxDist;

                if (!float.IsNaN(hitDist))
                    return Math.Clamp(hitDist, 0f, maxDist);
                if (!float.IsNaN(hitPoint.x))
                    return Math.Clamp(Vector3.Distance(from, hitPoint), 0f, maxDist);

                return 0f;
            }

            if (_useUnityFallback && _miUnityRaycast != null)
            {
                float clearDist = 0f;
                foreach (float frac in UnityFallbackFractions)
                {
                    float probeDist = maxDist * frac;
                    Vector3 to = from + dir * probeDist;
                    if (InvokeCast(from, radius, to, to - from, _blockMaskBox, ignoreStep))
                        return clearDist;
                    clearDist = probeDist;
                }
                return maxDist;
            }
        }
        catch (Exception ex)
        {
            if (!_warnedError)
            {
                _warnedError = true;
                _services.Log.Warning($"[AgyBridge][Nav] Physics distance probe failed: {ex.Message}");
            }
        }

        return maxDist;
    }

    /// <summary>
    /// Samples walkable surface elevation near (<paramref name="x"/>, <paramref name="refY"/>, <paramref name="z"/>).
    /// Returns false when no surface is hit or when the probe origin starts inside a solid collider (crates/walls).
    /// </summary>
    public bool TrySampleGroundHeight(
        float x,
        float refY,
        float z,
        out float groundY,
        float upOffset = 1.35f,
        float downDepth = 2.10f,
        float radius = 0.22f)
    {
        groundY = refY;
        if (!EnsureResolved())
            return false;

        Vector3 from = new(x, refY + upOffset, z);
        Vector3 to = new(x, refY - downDepth, z);

        try
        {
            if (!_useUnityFallback && _miSphereCast != null)
            {
                if (!TryRawSphereCast(from, radius, to, _groundOrBlockMaskBox, ignoreStep: false, out float hitDist, out Vector3 hitPoint))
                    return false;

                // Reject solid interiors (boxes/walls/columns engulfing refY + upOffset).
                if (!float.IsNaN(hitDist) && hitDist >= 0f && hitDist < 0.06f)
                    return false;

                if (!float.IsNaN(hitPoint.y))
                {
                    groundY = hitPoint.y;
                    return true;
                }

                if (!float.IsNaN(hitDist) && hitDist >= 0.06f)
                {
                    groundY = from.y - hitDist - radius * 0.5f;
                    return true;
                }

                return false;
            }

            if (_useUnityFallback && _miUnityRaycast != null)
            {
                if (!InvokeCast(from, radius, to, to - from, _groundOrBlockMaskBox, ignoreStep: false))
                    return false;

                Vector3 topCheck = new(x, from.y - 0.08f, z);
                if (InvokeCast(from, radius, topCheck, topCheck - from, _groundOrBlockMaskBox, ignoreStep: false))
                    return false;

                float lo = refY - downDepth;
                float hi = from.y - 0.08f;
                for (int i = 0; i < 4; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    Vector3 midPt = new(x, mid, z);
                    if (InvokeCast(from, radius, midPt, midPt - from, _groundOrBlockMaskBox, ignoreStep: false))
                        lo = mid;
                    else
                        hi = mid;
                }

                groundY = 0.5f * (lo + hi);
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!_warnedError)
            {
                _warnedError = true;
                _services.Log.Warning($"[AgyBridge][Nav] Ground height sample failed: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Validates slope/step limits and dual torso sphere casts between two walkable floor positions.
    /// Rejects sheer walls, balcony drop-offs, low under-stair wedges, and railings.
    /// </summary>
    public bool CanStep3D(Vector3 fromFloor, Vector3 toFloor, float bodyRadius = 0.36f)
    {
        float dx = toFloor.x - fromFloor.x;
        float dz = toFloor.z - fromFloor.z;
        float hDist = MathF.Sqrt(dx * dx + dz * dz);
        if (hDist < 1e-4f)
            return MathF.Abs(toFloor.y - fromFloor.y) <= 0.5f;

        float dy = toFloor.y - fromFloor.y;
        if (dy > MathF.Max(0.68f, hDist * 0.65f))
            return false;
        if (dy < -MathF.Max(0.95f, hDist * 0.78f))
            return false;

        Vector3 waistFrom = new(fromFloor.x, fromFloor.y + 0.68f, fromFloor.z);
        Vector3 waistTo = new(toFloor.x, toFloor.y + 0.68f, toFloor.z);
        if (IsBlocked(waistFrom, waistTo, bodyRadius, ignoreStep: true))
            return false;

        float chestRadius = MathF.Max(0.28f, bodyRadius - 0.03f);
        Vector3 chestFrom = new(fromFloor.x, fromFloor.y + 1.62f, fromFloor.z);
        Vector3 chestTo = new(toFloor.x, toFloor.y + 1.62f, toFloor.z);
        if (IsBlocked(chestFrom, chestTo, chestRadius, ignoreStep: true))
            return false;

        return true;
    }

    /// <summary>
    /// Distinguishes un-climbable full-height walls and staircase sides from low climbable ledges
    /// by checking whether both body height and overhead (y + 2.05m) rays are blocked.
    /// </summary>
    public bool IsTallWallAhead(Vector3 localPos, Vector3 dir, float checkDist = 2.2f)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f || !EnsureResolved())
            return false;

        dir = dir.normalized;
        Vector3 waist = new(localPos.x, localPos.y + 0.65f, localPos.z);
        Vector3 chest = new(localPos.x, localPos.y + 1.30f, localPos.z);
        Vector3 head = new(localPos.x, localPos.y + 2.05f, localPos.z);

        bool bodyBlocked = IsBlocked(waist, waist + dir * checkDist, 0.38f, ignoreStep: true)
            || IsBlocked(chest, chest + dir * checkDist, 0.36f, ignoreStep: true);
        if (!bodyBlocked)
            return false;

        float headDist = MathF.Min(checkDist, 2.6f);
        return IsBlocked(head, head + dir * headDist, 0.34f, ignoreStep: true);
    }

    /// <summary>
    /// Casts downward around <paramref name="worldPos"/> to verify walkable ground exists
    /// and avoid steering off ledges or cliffs. Returns true when sensor is unavailable.
    /// </summary>
    public bool HasGroundNear(Vector3 worldPos, float upOffset = 1.1f, float downDepth = 3.8f, float radius = 0.24f)
    {
        if (!EnsureResolved())
            return true;

        Vector3 from = new(worldPos.x, worldPos.y + upOffset, worldPos.z);
        Vector3 to = new(worldPos.x, worldPos.y - downDepth, worldPos.z);
        return InvokeCast(from, radius, to, to - from, _groundOrBlockMaskBox, ignoreStep: false);
    }

    private bool TryRawSphereCast(
        Vector3 from,
        float radius,
        Vector3 to,
        object maskBox,
        bool ignoreStep,
        out float hitDist,
        out Vector3 hitPoint)
    {
        hitDist = float.NaN;
        hitPoint = new Vector3(float.NaN, float.NaN, float.NaN);

        if (_miSphereCast == null)
            return false;

        _sphereArgs[0] = from;
        _sphereArgs[1] = radius;
        _sphereArgs[2] = to;
        _sphereArgs[3] = maskBox;
        _sphereArgs[4] = _hitBox;
        _sphereArgs[5] = ignoreStep ? BoxTrue : BoxFalse;
        _sphereArgs[6] = BoxFalse;
        object? res = _miSphereCast.Invoke(null, _sphereArgs);
        if (res is not bool hit || !hit)
            return false;

        object? boxedHit = _sphereArgs[4];
        if (boxedHit != null)
        {
            if (_fiHitDistance?.GetValue(boxedHit) is float d)
                hitDist = d;
            if (_fiHitPoint?.GetValue(boxedHit) is Vector3 pt)
                hitPoint = pt;
        }

        return true;
    }

    private bool InvokeCast(Vector3 from, float radius, Vector3 to, Vector3 delta, object maskBox, bool ignoreStep)
    {
        try
        {
            if (!_useUnityFallback && _miSphereCast != null)
                return TryRawSphereCast(from, radius, to, maskBox, ignoreStep, out _, out _);

            if (_useUnityFallback && _miUnityRaycast != null)
            {
                float dist = delta.magnitude;
                if (dist < 1e-4f) return false;
                _rayArgs[0] = from;
                _rayArgs[1] = delta / dist;
                _rayArgs[2] = dist;
                _rayArgs[3] = maskBox;
                object? res = _miUnityRaycast.Invoke(null, _rayArgs);
                return res is bool hit && hit;
            }
        }
        catch (Exception ex)
        {
            if (!_warnedError)
            {
                _warnedError = true;
                _services.Log.Warning($"[AgyBridge][Nav] Physics cast failed: {ex.Message}");
            }
        }

        return false;
    }
}
