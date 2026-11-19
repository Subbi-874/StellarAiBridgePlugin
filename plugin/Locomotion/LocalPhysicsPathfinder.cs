using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Real-time time-sliced 2.5D A* pathfinder built directly on world-space physics geometry
/// (<see cref="IPhysicsWalkabilityProbe"/>). Separates overlapping multi-story floors by Y-band,
/// prevents diagonal corner-cutting, and applies 3D string-pulling + corner wall standoff.
/// </summary>
public sealed class LocalPhysicsPathfinder
{
    private readonly struct Offset2D
    {
        public readonly int Dx;
        public readonly int Dz;
        public readonly float Cost;
        public readonly int OrthoA;
        public readonly int OrthoB;

        public Offset2D(int dx, int dz, float cost, int orthoA = -1, int orthoB = -1)
        {
            Dx = dx;
            Dz = dz;
            Cost = cost;
            OrthoA = orthoA;
            OrthoB = orthoB;
        }
    }

    private readonly struct PathPoint
    {
        public readonly Vector3 Pos;
        public readonly bool IsDropOff;
        public readonly bool RequiresVault;

        public PathPoint(Vector3 pos, bool isDropOff = false, bool requiresVault = false)
        {
            Pos = pos;
            IsDropOff = isDropOff;
            RequiresVault = requiresVault;
        }
    }

    private sealed class GridNode
    {
        public long Key;
        public int Ix;
        public int Iz;
        public int YBand;
        public float X;
        public float Y;
        public float Z;
        public float G;
        public float H;
        public int ParentIndex;
        public int Depth;
        public bool Closed;
        public bool ViaDropOff;
        public bool ViaDropVault;
    }

    private static readonly Offset2D[] CardinalOffsets =
    {
        new(1, 0, 1.0f),
        new(-1, 0, 1.0f),
        new(0, 1, 1.0f),
        new(0, -1, 1.0f),
    };

    // Each diagonal requires both adjacent cardinal directions (OrthoA, OrthoB) to be traversable.
    private static readonly Offset2D[] DiagonalOffsets =
    {
        new(1, 1, 1.41421356f, 0, 2),
        new(1, -1, 1.41421356f, 0, 3),
        new(-1, 1, 1.41421356f, 1, 2),
        new(-1, -1, 1.41421356f, 1, 3),
    };

    private readonly IPluginServices? _services;
    private readonly IPhysicsWalkabilityProbe _probe;

    public float CellSize { get; set; } = 1.05f;
    public float YBandHeight { get; set; } = 2.60f;
    public int ExpandBudgetPerTick { get; set; } = 120;
    public int MaxExpandedNodes { get; set; } = 3200;
    public float MaxSearchSpan { get; set; } = 75.0f;
    public float SearchMargin { get; set; } = 18.0f;
    public float MinStartRadius { get; set; } = 24.0f;
    public float MaxSafeDropHeight { get; set; } = 8.5f;

    // Active double-buffered path state
    private readonly List<PathPoint> _activeWaypoints = new(32);
    private int _activeIndex;
    private long _activeTargetKey;
    private Vector3 _activeGoal;
    private bool _activeIsPartial;
    private float _wpStuckTimer;
    private float _bestWpDist = float.MaxValue;
    private string _activeDiag = "";

    // Time-sliced A* search state
    private bool _searchInProgress;
    private Vector3 _searchStart;
    private Vector3 _searchGoal;
    private float _searchMinX;
    private float _searchMaxX;
    private float _searchMinZ;
    private float _searchMaxZ;
    private float _startH;
    private int _expandedCount;
    private int _bestClosedFallbackIdx = -1;
    private float _bestClosedFallbackH = float.MaxValue;
    private float _searchCooldown;
    private float _elapsedSec;

    private readonly List<GridNode> _nodes = new(512);
    private readonly Dictionary<long, int> _nodeByKey = new(512);
    private readonly PriorityQueue<int, float> _open = new(256);
    private readonly Dictionary<long, float> _groundCache = new(1024);
    private readonly HashSet<long> _noGroundCache = new(512);
    private readonly Dictionary<long, float> _dropGroundCache = new(512);
    private readonly HashSet<long> _noDropGroundCache = new(256);
    private readonly Dictionary<long, float> _blockedUntilTime = new(64);
    private readonly bool[] _cardinalOk = new bool[4];

    public LocalPhysicsPathfinder(IPluginServices? services, IPhysicsWalkabilityProbe probe)
    {
        _services = services;
        _probe = probe;
    }

    public bool HasActivePath => _activeWaypoints.Count > 0 && _activeIndex < _activeWaypoints.Count;
    public bool IsSearching => _searchInProgress;
    public bool LastSearchExhaustedSmallIsland { get; private set; }
    public bool ActiveSegmentIsDropOff => HasActivePath && _activeWaypoints[_activeIndex].IsDropOff;
    public bool ActiveSegmentRequiresVault => HasActivePath && _activeWaypoints[_activeIndex].RequiresVault;
    public int ActiveWaypointIndex => _activeIndex;
    public int ActiveWaypointCount => _activeWaypoints.Count;

    public void Reset()
    {
        _activeWaypoints.Clear();
        _activeIndex = 0;
        _activeTargetKey = 0L;
        _activeGoal = default;
        _activeIsPartial = false;
        _wpStuckTimer = 0f;
        _bestWpDist = float.MaxValue;
        _activeDiag = "";
        LastSearchExhaustedSmallIsland = false;

        ClearSearchState();
        _blockedUntilTime.Clear();
        _searchCooldown = 0f;
    }

    private void ClearSearchState()
    {
        _searchInProgress = false;
        _nodes.Clear();
        _nodeByKey.Clear();
        _open.Clear();
        _groundCache.Clear();
        _noGroundCache.Clear();
        _dropGroundCache.Clear();
        _noDropGroundCache.Clear();
        _expandedCount = 0;
        _bestClosedFallbackIdx = -1;
        _bestClosedFallbackH = float.MaxValue;
    }

    public bool UpdateAndGetWaypoint(
        float dt,
        Vector3 localPos,
        Vector3 goalPos,
        long targetKey,
        out Vector3 waypoint,
        out bool isIntermediate,
        out bool justCompleted,
        out string diagNote)
    {
        waypoint = goalPos;
        isIntermediate = false;
        justCompleted = false;
        diagNote = _activeDiag;

        if (!_probe.IsReady)
            return false;

        dt = Math.Clamp(dt, 0f, 0.25f);
        _elapsedSec += dt;
        if (_searchCooldown > 0f)
            _searchCooldown = MathF.Max(0f, _searchCooldown - dt);

        if (_activeTargetKey != targetKey)
        {
            _activeTargetKey = targetKey;
            _activeWaypoints.Clear();
            _activeIndex = 0;
            _activeIsPartial = false;
            _wpStuckTimer = 0f;
            _bestWpDist = float.MaxValue;
            LastSearchExhaustedSmallIsland = false;
            ClearSearchState();
            _searchCooldown = 0f;
        }

        if (HasActivePath)
        {
            AdvanceActiveWaypoints(localPos);
            TrackProgressOrMarkBlocked(dt, localPos);
        }

        bool goalShifted = !HasActivePath
            || HorizontalDist(goalPos, _activeGoal) >= 3.2f
            || MathF.Abs(goalPos.y - _activeGoal.y) >= 1.8f;

        bool needFrontierExtension = _activeIsPartial
            && HasActivePath
            && (_activeIndex >= _activeWaypoints.Count - 2
                || HorizontalDist(localPos, _activeWaypoints[^1].Pos) <= 6.0f);

        if (!_searchInProgress && _searchCooldown <= 0f && (!HasActivePath || goalShifted || needFrontierExtension))
        {
            BeginSearch(localPos, goalPos);
        }

        if (_searchInProgress)
        {
            bool finished = StepSearch(ExpandBudgetPerTick);
            if (finished)
                justCompleted = true;
        }

        if (!HasActivePath)
        {
            diagNote = _activeDiag;
            return false;
        }

        waypoint = _activeWaypoints[_activeIndex].Pos;
        isIntermediate = _activeIndex < _activeWaypoints.Count - 1;
        diagNote = _activeDiag;
        return true;
    }

    private void AdvanceActiveWaypoints(Vector3 localPos)
    {
        while (_activeIndex < _activeWaypoints.Count - 1)
        {
            Vector3 wp = _activeWaypoints[_activeIndex].Pos;
            PathPoint nextPt = _activeWaypoints[_activeIndex + 1];
            Vector3 nextWp = nextPt.Pos;
            float dCur = HorizontalDist(localPos, wp);
            bool onSameBand = MathF.Abs(wp.y - localPos.y) <= 1.85f;
            bool reachedTight = onSameBand && dCur <= 0.65f;
            bool clearTurn = onSameBand && !nextPt.IsDropOff && dCur <= 1.50f && CanShortcut3D(localPos, nextWp);
            if (reachedTight || clearTurn)
            {
                _activeIndex++;
                _wpStuckTimer = 0f;
                _bestWpDist = HorizontalDist(localPos, _activeWaypoints[_activeIndex].Pos);
            }
            else
            {
                break;
            }
        }

        // Short-range line-of-sight lookahead to advance cleanly past rounded turns without skipping drop-offs or floors
        if (_activeIndex < _activeWaypoints.Count - 1 && !_activeWaypoints[_activeIndex].IsDropOff)
        {
            int maxLook = Math.Min(_activeIndex + 2, _activeWaypoints.Count - 1);
            for (int k = maxLook; k > _activeIndex; k--)
            {
                bool crossesDrop = false;
                for (int j = _activeIndex + 1; j <= k; j++)
                {
                    if (_activeWaypoints[j].IsDropOff)
                    {
                        crossesDrop = true;
                        break;
                    }
                }
                if (crossesDrop)
                    continue;

                Vector3 cand = _activeWaypoints[k].Pos;
                if (HorizontalDist(localPos, cand) <= 10.0f && CanShortcut3D(localPos, cand))
                {
                    _activeIndex = k;
                    _wpStuckTimer = 0f;
                    _bestWpDist = HorizontalDist(localPos, cand);
                    break;
                }
            }
        }
    }

    private void TrackProgressOrMarkBlocked(float dt, Vector3 localPos)
    {
        Vector3 curWp = _activeWaypoints[_activeIndex].Pos;
        float dist = HorizontalDist(localPos, curWp);
        if (dist < _bestWpDist - 0.30f)
        {
            _bestWpDist = dist;
            _wpStuckTimer = 0f;
            return;
        }

        _wpStuckTimer += dt;
        if (_wpStuckTimer < 1.20f)
            return;

        float dx = curWp.x - localPos.x;
        float dz = curWp.z - localPos.z;
        float hLen = MathF.Sqrt(dx * dx + dz * dz);
        float expiry = _elapsedSec + 15.0f;
        int yBand = ToYBand(localPos.y);
        if (hLen > 0.1f)
        {
            float step = MathF.Min(1.4f, hLen);
            float bx = localPos.x + (dx / hLen) * step;
            float bz = localPos.z + (dz / hLen) * step;
            int bIx = ToCell(bx);
            int bIz = ToCell(bz);
            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oz = -1; oz <= 1; oz++)
                {
                    _blockedUntilTime[PackKey(bIx + ox, bIz + oz, yBand)] = expiry;
                }
            }
        }

        if (dist <= 2.2f)
        {
            _blockedUntilTime[PackKey(ToCell(curWp.x), ToCell(curWp.z), ToYBand(curWp.y))] = expiry;
        }

        _activeWaypoints.Clear();
        _activeIndex = 0;
        _wpStuckTimer = 0f;
        _bestWpDist = float.MaxValue;
        _searchCooldown = 0f;
        ClearSearchState();
    }

    private void BeginSearch(Vector3 localPos, Vector3 goalPos)
    {
        ClearSearchState();

        float startY = _probe.TrySampleGroundHeight(localPos.x, localPos.y, localPos.z, out float gy)
            ? gy
            : localPos.y;
        _searchStart = new Vector3(localPos.x, startY, localPos.z);
        _searchGoal = goalPos;

        float centerX = 0.5f * (_searchStart.x + goalPos.x);
        float centerZ = 0.5f * (_searchStart.z + goalPos.z);
        float halfSpan = 0.5f * MaxSearchSpan;

        float rawMinX = MathF.Min(_searchStart.x - MinStartRadius, goalPos.x - SearchMargin);
        float rawMaxX = MathF.Max(_searchStart.x + MinStartRadius, goalPos.x + SearchMargin);
        float rawMinZ = MathF.Min(_searchStart.z - MinStartRadius, goalPos.z - SearchMargin);
        float rawMaxZ = MathF.Max(_searchStart.z + MinStartRadius, goalPos.z + SearchMargin);

        float clampMinX = MathF.Min(_searchStart.x - MinStartRadius, centerX - halfSpan);
        float clampMaxX = MathF.Max(_searchStart.x + MinStartRadius, centerX + halfSpan);
        float clampMinZ = MathF.Min(_searchStart.z - MinStartRadius, centerZ - halfSpan);
        float clampMaxZ = MathF.Max(_searchStart.z + MinStartRadius, centerZ + halfSpan);

        _searchMinX = MathF.Max(rawMinX, clampMinX);
        _searchMaxX = MathF.Min(rawMaxX, clampMaxX);
        _searchMinZ = MathF.Max(rawMinZ, clampMinZ);
        _searchMaxZ = MathF.Min(rawMaxZ, clampMaxZ);

        int sx = ToCell(_searchStart.x);
        int sz = ToCell(_searchStart.z);
        int sy = ToYBand(_searchStart.y);
        long sKey = PackKey(sx, sz, sy);
        _startH = ComputeHeuristic(_searchStart.x, _searchStart.y, _searchStart.z, goalPos);

        var startNode = new GridNode
        {
            Key = sKey,
            Ix = sx,
            Iz = sz,
            YBand = sy,
            X = _searchStart.x,
            Y = _searchStart.y,
            Z = _searchStart.z,
            G = 0f,
            H = _startH,
            ParentIndex = -1,
            Depth = 0,
            Closed = false
        };

        _nodes.Add(startNode);
        _nodeByKey[sKey] = 0;
        _open.Enqueue(0, startNode.G + startNode.H);
        _searchInProgress = true;
    }

    private bool StepSearch(int budget)
    {
        int expandedThisTick = 0;

        while (_open.Count > 0 && expandedThisTick < budget && _expandedCount < MaxExpandedNodes)
        {
            if (!_open.TryDequeue(out int curIdx, out float queuedF))
                break;

            GridNode cur = _nodes[curIdx];
            if (cur.Closed || queuedF > cur.G + cur.H + 1e-3f)
                continue;

            cur.Closed = true;
            _expandedCount++;
            expandedThisTick++;

            if (cur.Depth > 0
                && HorizontalDistXZ(cur.X, cur.Z, _searchStart.x, _searchStart.z) >= 2.2f
                && cur.H <= _startH - 1.8f
                && cur.H < _bestClosedFallbackH)
            {
                _bestClosedFallbackH = cur.H;
                _bestClosedFallbackIdx = curIdx;
            }

            if (HorizontalDistXZ(cur.X, cur.Z, _searchGoal.x, _searchGoal.z) <= 2.1f
                && MathF.Abs(cur.Y - _searchGoal.y) <= 2.0f)
            {
                FinalizeSearch(curIdx, reachedGoal: true);
                return true;
            }

            if (cur.Depth == 0)
            {
                ExpandNode(curIdx, cur, radius: 0.34f, allowCornerEscapeDiagonals: true, out int added);
                if (added == 0)
                {
                    // Allow narrower escape radius when spawned flush against walls or crates
                    ExpandNode(curIdx, cur, radius: 0.24f, allowCornerEscapeDiagonals: true, out _);
                }
            }
            else
            {
                ExpandNode(curIdx, cur, radius: 0.34f, allowCornerEscapeDiagonals: false, out _);
            }
        }

        if (_open.Count > 0 && _expandedCount < MaxExpandedNodes)
            return false;

        int fallbackIdx = PickFrontierOrClosedFallback(out bool hasFrontier);
        if (fallbackIdx > 0)
        {
            FinalizeSearch(fallbackIdx, reachedGoal: false, hasFrontier: hasFrontier);
            return true;
        }

        LastSearchExhaustedSmallIsland = _expandedCount <= 45;
        ClearSearchState();
        _searchCooldown = 0.85f;
        _activeDiag = "local_astar(no_path)";
        return true;
    }

    private void ExpandNode(int curIdx, GridNode cur, float radius, bool allowCornerEscapeDiagonals, out int addedCount)
    {
        addedCount = 0;
        Vector3 curPos = new(cur.X, cur.Y, cur.Z);

        for (int i = 0; i < 4; i++)
        {
            _cardinalOk[i] = false;
            var off = CardinalOffsets[i];
            int nx = cur.Ix + off.Dx;
            int nz = cur.Iz + off.Dz;
            if (TryEvaluateNeighbor(curIdx, cur, curPos, nx, nz, off.Cost, radius, out bool walkableOk, allowDropOff: true))
            {
                _cardinalOk[i] = walkableOk;
                addedCount++;
            }
            else if (TryEvaluateDropNeighbor(curIdx, cur, curPos, cur.Ix + off.Dx * 2, cur.Iz + off.Dz * 2, 2.0f, radius))
            {
                // Vault-drop 2 cells out clears wide awning eaves or balcony parapets onto lower street ground.
                addedCount++;
            }
        }

        for (int i = 0; i < 4; i++)
        {
            var off = DiagonalOffsets[i];
            int nx = cur.Ix + off.Dx;
            int nz = cur.Iz + off.Dz;
            if (allowCornerEscapeDiagonals || (_cardinalOk[off.OrthoA] && _cardinalOk[off.OrthoB]))
            {
                if (TryEvaluateNeighbor(curIdx, cur, curPos, nx, nz, off.Cost, radius, out _, allowDropOff: true))
                    addedCount++;
            }
            else if (cur.Depth <= 3)
            {
                if (TryEvaluateDropNeighbor(curIdx, cur, curPos, nx, nz, off.Cost, radius))
                    addedCount++;
            }
        }
    }

    private bool TryEvaluateNeighbor(
        int curIdx,
        GridNode cur,
        Vector3 curPos,
        int nx,
        int nz,
        float stepCostFactor,
        float radius,
        out bool walkableOk,
        bool allowDropOff)
    {
        walkableOk = false;

        if (!TryGetCellGround(nx, nz, cur.Y, out float ny))
        {
            return allowDropOff && TryEvaluateDropNeighbor(curIdx, cur, curPos, nx, nz, stepCostFactor, radius);
        }

        float wx = nx * CellSize;
        float wz = nz * CellSize;
        Vector3 candPos = new(wx, ny, wz);

        if (!_probe.CanStep3D(curPos, candPos, radius))
        {
            return allowDropOff && TryEvaluateDropNeighbor(curIdx, cur, curPos, nx, nz, stepCostFactor, radius);
        }

        if (MathF.Abs(ny - cur.Y) > 0.30f)
        {
            float midX = 0.5f * (cur.X + wx);
            float midY = 0.5f * (cur.Y + ny);
            float midZ = 0.5f * (cur.Z + wz);
            if (!_probe.TrySampleGroundHeight(midX, midY, midZ, out float midGy)
                || MathF.Abs(midGy - midY) > 0.24f)
            {
                return allowDropOff && TryEvaluateDropNeighbor(curIdx, cur, curPos, nx, nz, stepCostFactor, radius);
            }
        }

        walkableOk = true;

        int nyBand = ToYBand(ny);
        long nKey = PackKey(nx, nz, nyBand);

        float penalty = GetBlockedPenalty(nKey);
        float stepDist = stepCostFactor * CellSize;
        float tentativeG = cur.G + stepDist + MathF.Abs(ny - cur.Y) * 0.45f + penalty;

        EnqueueOrRelaxNode(curIdx, cur, nx, nz, nyBand, nKey, wx, ny, wz, tentativeG, viaDropOff: false, viaDropVault: false);
        return true;
    }

    private bool TryEvaluateDropNeighbor(
        int curIdx,
        GridNode cur,
        Vector3 curPos,
        int nx,
        int nz,
        float stepCostFactor,
        float radius)
    {
        if (!TryGetDropCellGround(nx, nz, cur.Y, MaxSafeDropHeight, out float dropY))
            return false;

        float wx = nx * CellSize;
        float wz = nz * CellSize;
        Vector3 dropPos = new(wx, dropY, wz);
        if (!_probe.CanDropOff3D(curPos, dropPos, radius, out bool requiresVault))
            return false;

        int nyBand = ToYBand(dropY);
        long nKey = PackKey(nx, nz, nyBand);

        float penalty = GetBlockedPenalty(nKey);
        float drop = cur.Y - dropY;
        // High drop penalty ensures connected stairs or ramps are always preferred whenever they exist.
        float dropPenalty = (requiresVault ? 28.0f : 22.0f) + drop * 3.0f + (stepCostFactor > 1.5f ? 3.0f : 0f);
        float stepDist = stepCostFactor * CellSize;
        float tentativeG = cur.G + stepDist + dropPenalty + penalty;

        EnqueueOrRelaxNode(curIdx, cur, nx, nz, nyBand, nKey, wx, dropY, wz, tentativeG, viaDropOff: true, viaDropVault: requiresVault);
        return true;
    }

    private float GetBlockedPenalty(long key)
    {
        if (_blockedUntilTime.TryGetValue(key, out float until))
        {
            if (until > _elapsedSec)
                return 20.0f;
            _blockedUntilTime.Remove(key);
        }
        return 0f;
    }

    private void EnqueueOrRelaxNode(
        int curIdx,
        GridNode cur,
        int nx,
        int nz,
        int nyBand,
        long nKey,
        float wx,
        float wy,
        float wz,
        float tentativeG,
        bool viaDropOff,
        bool viaDropVault)
    {
        if (_nodeByKey.TryGetValue(nKey, out int existingIdx))
        {
            GridNode existing = _nodes[existingIdx];
            if (existing.Closed || tentativeG >= existing.G - 1e-4f)
                return;

            existing.G = tentativeG;
            existing.X = wx;
            existing.Y = wy;
            existing.Z = wz;
            existing.ParentIndex = curIdx;
            existing.Depth = cur.Depth + 1;
            existing.ViaDropOff = viaDropOff;
            existing.ViaDropVault = viaDropVault;
            _open.Enqueue(existingIdx, existing.G + existing.H);
            return;
        }

        float h = ComputeHeuristic(wx, wy, wz, _searchGoal);
        var nextNode = new GridNode
        {
            Key = nKey,
            Ix = nx,
            Iz = nz,
            YBand = nyBand,
            X = wx,
            Y = wy,
            Z = wz,
            G = tentativeG,
            H = h,
            ParentIndex = curIdx,
            Depth = cur.Depth + 1,
            Closed = false,
            ViaDropOff = viaDropOff,
            ViaDropVault = viaDropVault
        };

        int nextIdx = _nodes.Count;
        _nodes.Add(nextNode);
        _nodeByKey[nKey] = nextIdx;
        _open.Enqueue(nextIdx, nextNode.G + nextNode.H);
    }

    private int PickFrontierOrClosedFallback(out bool hasFrontier)
    {
        hasFrontier = false;
        int bestOpenIdx = -1;
        float bestOpenH = float.MaxValue;

        foreach (var entry in _open.UnorderedItems)
        {
            int idx = entry.Element;
            GridNode node = _nodes[idx];
            if (node.Closed || entry.Priority > node.G + node.H + 1e-3f)
                continue;

            if (node.Depth == 0)
                continue;

            if (node.H < bestOpenH && HorizontalDistXZ(node.X, node.Z, _searchStart.x, _searchStart.z) >= 0.8f)
            {
                bestOpenH = node.H;
                bestOpenIdx = idx;
            }
        }

        if (bestOpenIdx > 0)
        {
            hasFrontier = true;
            return bestOpenIdx;
        }

        return _bestClosedFallbackIdx;
    }

    private void FinalizeSearch(int endIdx, bool reachedGoal, bool hasFrontier = false)
    {
        var raw = new List<PathPoint>(64);
        int trace = endIdx;
        int guard = 0;
        while (trace >= 0 && guard++ <= MaxExpandedNodes + 4)
        {
            GridNode n = _nodes[trace];
            raw.Add(new PathPoint(new Vector3(n.X, n.Y, n.Z), n.ViaDropOff, n.ViaDropVault));
            trace = n.ParentIndex;
        }
        raw.Reverse();

        if (reachedGoal && raw.Count > 0)
        {
            PathPoint last = raw[^1];
            float remH = HorizontalDist(last.Pos, _searchGoal);
            if (remH > 0.25f && (remH < 0.5f || _probe.CanStep3D(last.Pos, _searchGoal, 0.30f)))
                raw.Add(new PathPoint(_searchGoal));
        }

        var smoothed = SmoothPath3D(raw);
        ApplyCornerStandoff(smoothed);

        bool hasDrop = false;
        foreach (var wp in smoothed)
        {
            if (wp.IsDropOff)
            {
                hasDrop = true;
                break;
            }
        }

        _activeWaypoints.Clear();
        _activeWaypoints.AddRange(smoothed);
        _activeGoal = _searchGoal;
        _activeIsPartial = !reachedGoal;
        LastSearchExhaustedSmallIsland = false;
        _activeIndex = (_activeWaypoints.Count > 1 && HorizontalDist(_searchStart, _activeWaypoints[0].Pos) < 1.35f) ? 1 : 0;
        _wpStuckTimer = 0f;
        _bestWpDist = _activeIndex < _activeWaypoints.Count
            ? HorizontalDist(_searchStart, _activeWaypoints[_activeIndex].Pos)
            : float.MaxValue;
        _searchCooldown = 0.20f;
        string dropTag = hasDrop ? ",+drop" : "";
        _activeDiag = reachedGoal
            ? $"local_astar({smoothed.Count}wps,{_expandedCount}n{dropTag})"
            : $"local_astar_partial({(hasFrontier ? "frontier" : "best")},{_expandedCount}n{dropTag})";

        ClearSearchState();
    }

    private List<PathPoint> SmoothPath3D(List<PathPoint> raw)
    {
        if (raw.Count <= 2)
            return new List<PathPoint>(raw);

        var result = new List<PathPoint>(raw.Count) { raw[0] };
        int anchor = 0;

        while (anchor < raw.Count - 1)
        {
            int nextAnchor = anchor + 1;
            int maxCand = raw.Count - 1;
            for (int k = anchor + 1; k < raw.Count; k++)
            {
                if (raw[k].IsDropOff)
                {
                    maxCand = (k == anchor + 1) ? anchor + 1 : k - 1;
                    break;
                }
            }

            for (int cand = maxCand; cand > anchor + 1; cand--)
            {
                if (CanShortcut3D(raw[anchor].Pos, raw[cand].Pos))
                {
                    nextAnchor = cand;
                    break;
                }
            }

            result.Add(raw[nextAnchor]);
            anchor = nextAnchor;
        }

        return result;
    }

    private bool CanShortcut3D(Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        float hDist = MathF.Sqrt(dx * dx + dz * dz);
        if (hDist < 0.05f)
            return true;
        if (hDist > 14.0f)
            return false;

        // Prevent pulling straight off staircases/ramps onto upper or lower ledges
        if (MathF.Abs(b.y - a.y) > MathF.Max(1.05f, hDist * 0.42f))
            return false;

        Vector3 waistFrom = new(a.x, a.y + 0.68f, a.z);
        Vector3 waistTo = new(b.x, b.y + 0.68f, b.z);
        if (_probe.IsBlocked(waistFrom, waistTo, 0.44f, ignoreStep: true))
            return false;

        Vector3 chestFrom = new(a.x, a.y + 1.48f, a.z);
        Vector3 chestTo = new(b.x, b.y + 1.48f, b.z);
        if (_probe.IsBlocked(chestFrom, chestTo, 0.40f, ignoreStep: true))
            return false;

        int interiorSamples = Math.Max(1, (int)MathF.Ceiling(hDist / 0.85f) - 1);
        float dy = b.y - a.y;
        for (int s = 1; s <= interiorSamples; s++)
        {
            float t = (float)s / (interiorSamples + 1);
            float sx = a.x + dx * t;
            float sy = a.y + dy * t;
            float sz = a.z + dz * t;

            if (!_probe.TrySampleGroundHeight(sx, sy, sz, out float gy) || MathF.Abs(gy - sy) > 0.38f)
                return false;

            if (_blockedUntilTime.Count > 0)
            {
                long k = PackKey(ToCell(sx), ToCell(sz), ToYBand(sy));
                if (_blockedUntilTime.TryGetValue(k, out float until) && until > _elapsedSec)
                    return false;
            }
        }

        return true;
    }

    private void ApplyCornerStandoff(List<PathPoint> pts)
    {
        if (pts.Count < 3)
            return;

        for (int i = 1; i < pts.Count - 1; i++)
        {
            if (pts[i - 1].IsDropOff || pts[i].IsDropOff || pts[i + 1].IsDropOff)
                continue;

            Vector3 prev = pts[i - 1].Pos;
            Vector3 cur = pts[i].Pos;
            Vector3 next = pts[i + 1].Pos;

            Vector3 inDir = NormalizeXZ(cur.x - prev.x, cur.z - prev.z);
            Vector3 outDir = NormalizeXZ(next.x - cur.x, next.z - cur.z);
            Vector3 chord = NormalizeXZ(inDir.x + outDir.x, inDir.z + outDir.z);
            if (chord.x * chord.x + chord.z * chord.z < 1e-4f)
                chord = outDir;
            if (chord.x * chord.x + chord.z * chord.z < 1e-4f)
                continue;

            Vector3 right = new(chord.z, 0f, -chord.x);
            Vector3 waistCur = new(cur.x, cur.y + 0.72f, cur.z);
            Vector3 leftProbe = new(waistCur.x - right.x * 0.95f, waistCur.y, waistCur.z - right.z * 0.95f);
            Vector3 rightProbe = new(waistCur.x + right.x * 0.95f, waistCur.y, waistCur.z + right.z * 0.95f);

            bool leftBlocked = _probe.IsBlocked(waistCur, leftProbe, 0.34f, ignoreStep: true);
            bool rightBlocked = _probe.IsBlocked(waistCur, rightProbe, 0.34f, ignoreStep: true);
            if (leftBlocked == rightBlocked)
                continue;

            float pushSign = leftBlocked ? 1f : -1f;
            float cx = cur.x + right.x * pushSign * 0.42f;
            float cz = cur.z + right.z * pushSign * 0.42f;

            if (!_probe.TrySampleGroundHeight(cx, cur.y, cz, out float pushedY) || MathF.Abs(pushedY - cur.y) > 0.35f)
                continue;

            Vector3 cand = new(cx, pushedY, cz);
            Vector3 waistCand = new(cx, pushedY + 0.72f, cz);
            if (_probe.IsBlocked(waistCur, waistCand, 0.34f, ignoreStep: true))
                continue;

            if (!_probe.CanStep3D(prev, cand, 0.38f) || !_probe.CanStep3D(cand, next, 0.38f))
                continue;

            pts[i] = new PathPoint(cand, pts[i].IsDropOff, pts[i].RequiresVault);
        }
    }

    private bool TryGetCellGround(int ix, int iz, float refY, out float groundY)
    {
        float cx = ix * CellSize;
        float cz = iz * CellSize;
        if (cx < _searchMinX || cx > _searchMaxX || cz < _searchMinZ || cz > _searchMaxZ)
        {
            groundY = refY;
            return false;
        }

        int qy = (int)MathF.Floor(refY / 1.30f);
        long gKey = PackKey(ix, iz, qy);

        if (_groundCache.TryGetValue(gKey, out groundY))
            return true;
        if (_noGroundCache.Contains(gKey))
            return false;

        if (_probe.TrySampleGroundHeight(cx, refY, cz, out groundY))
        {
            _groundCache[gKey] = groundY;
            return true;
        }

        _noGroundCache.Add(gKey);
        return false;
    }

    private bool TryGetDropCellGround(int ix, int iz, float refY, float maxDropDepth, out float groundY)
    {
        float cx = ix * CellSize;
        float cz = iz * CellSize;
        if (cx < _searchMinX || cx > _searchMaxX || cz < _searchMinZ || cz > _searchMaxZ)
        {
            groundY = refY;
            return false;
        }

        int qy = (int)MathF.Floor(refY / 1.30f);
        long gKey = PackKey(ix, iz, qy);

        if (_dropGroundCache.TryGetValue(gKey, out groundY))
            return true;
        if (_noDropGroundCache.Contains(gKey))
            return false;

        if (_probe.TrySampleSafeDropGround(cx, refY, cz, maxDropDepth, out groundY))
        {
            _dropGroundCache[gKey] = groundY;
            return true;
        }

        _noDropGroundCache.Add(gKey);
        return false;
    }

    private float ComputeHeuristic(float x, float y, float z, Vector3 goal)
    {
        float dx = MathF.Abs(goal.x - x);
        float dz = MathF.Abs(goal.z - z);
        float minD = MathF.Min(dx, dz);
        float maxD = MathF.Max(dx, dz);
        float octile = maxD + 0.41421356f * minD;
        return (octile + MathF.Abs(goal.y - y) * 1.15f) * 1.08f;
    }

    private int ToCell(float coord) => (int)MathF.Round(coord / CellSize);
    private int ToYBand(float y) => (int)MathF.Floor(y / YBandHeight);

    private static long PackKey(int ix, int iz, int yBand)
    {
        return (((long)(ix & 0x3FFFFF)) << 42)
             | (((long)(iz & 0x3FFFFF)) << 20)
             | (uint)(yBand & 0xFFFFF);
    }

    private static float HorizontalDist(Vector3 a, Vector3 b) => HorizontalDistXZ(a.x, a.z, b.x, b.z);

    private static float HorizontalDistXZ(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static Vector3 NormalizeXZ(float x, float z)
    {
        float lenSq = x * x + z * z;
        if (lenSq < 1e-8f)
            return default;
        float inv = 1f / MathF.Sqrt(lenSq);
        return new Vector3(x * inv, 0f, z * inv);
    }
}
