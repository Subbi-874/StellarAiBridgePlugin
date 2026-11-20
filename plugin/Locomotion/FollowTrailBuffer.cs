using System;
using System.Collections.Generic;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Bounded breadcrumb buffer recording grounded footsteps / safe landing positions
/// of the followed target so bots can cross stepping-stone platforms and cliffs
/// along the exact path the leader walked instead of cutting corners into thin air.
/// </summary>
public sealed class FollowTrailBuffer
{
    public readonly record struct TrailPoint(float X, float Y, float Z);

    private const int MaxSamples = 24;
    private const float MinRecordHorizSq = 2.2f * 2.2f;
    private const float MinRecordVert = 1.5f;
    private const float ForceRecordHorizSq = 4.0f * 4.0f;

    private readonly List<TrailPoint> _points = new(MaxSamples);

    public int Count => _points.Count;

    public void Clear() => _points.Clear();

    public void RecordTargetSample(float x, float y, float z, bool targetGrounded)
    {
        if (_points.Count == 0)
        {
            if (targetGrounded)
                _points.Add(new TrailPoint(x, y, z));
            return;
        }

        TrailPoint last = _points[^1];
        float dx = x - last.X;
        float dz = z - last.Z;
        float horizSq = dx * dx + dz * dz;
        float dy = MathF.Abs(y - last.Y);

        if (horizSq < MinRecordHorizSq && dy < MinRecordVert)
            return;

        if (!targetGrounded && horizSq < ForceRecordHorizSq)
            return;

        if (_points.Count >= MaxSamples)
            _points.RemoveAt(0);
        _points.Add(new TrailPoint(x, y, z));
    }

    public void AdvancePassed(float lx, float ly, float lz)
    {
        TrimReachedHead(lx, ly, lz);
        if (_points.Count <= 1)
            return;

        // Fast-forward if a later breadcrumb on roughly the same floor is clearly closer than head[0]
        // (e.g., bot jumped directly onto platform C, skipping B).
        float headDist = HorizDist(lx, lz, _points[0].X, _points[0].Z);
        int fastForwardIdx = -1;
        for (int i = 1; i < _points.Count; i++)
        {
            TrailPoint cand = _points[i];
            if (MathF.Abs(cand.Y - ly) > 2.5f)
                continue;
            float d = HorizDist(lx, lz, cand.X, cand.Z);
            if (d <= headDist - 1.5f)
            {
                fastForwardIdx = i;
                headDist = d;
            }
        }
        if (fastForwardIdx > 0)
        {
            _points.RemoveRange(0, fastForwardIdx);
            TrimReachedHead(lx, ly, lz);
        }
    }

    public bool TryGetNextStep(
        float lx,
        float ly,
        float lz,
        float tx,
        float ty,
        float tz,
        bool useTrailForGapOrElevation,
        out TrailPoint nextPt)
    {
        nextPt = default;
        if (!useTrailForGapOrElevation || _points.Count == 0)
            return false;

        float localToTarget = HorizDist(lx, lz, tx, tz);
        for (int i = 0; i < _points.Count; i++)
        {
            TrailPoint p = _points[i];
            if (IsNear(lx, ly, lz, p.X, p.Y, p.Z, 2.4f, 2.2f))
                continue;

            float ptToTarget = HorizDist(p.X, p.Z, tx, tz);
            if (ptToTarget < 2.2f)
                continue;

            if (ptToTarget > localToTarget + 6.0f)
                continue;

            nextPt = p;
            return true;
        }

        return false;
    }

    private void TrimReachedHead(float lx, float ly, float lz)
    {
        while (_points.Count > 0)
        {
            TrailPoint first = _points[0];
            if (IsNear(lx, ly, lz, first.X, first.Y, first.Z, 2.5f, 2.3f))
                _points.RemoveAt(0);
            else
                break;
        }
    }

    private static bool IsNear(float ax, float ay, float az, float bx, float by, float bz, float horizRadius, float vertTol)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return (dx * dx + dz * dz) <= horizRadius * horizRadius && MathF.Abs(ay - by) <= vertTol;
    }

    private static float HorizDist(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
