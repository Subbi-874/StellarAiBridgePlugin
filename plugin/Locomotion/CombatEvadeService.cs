using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Real-time Just-Dodge combat assistant when Auto-Combat ('G') is active.
/// Hooks Panda.ZGame.ZWarningEffectShowInfo (floor AoEs), ZUISkillWarningShowInfo (boss danger flashes),
/// and ZEffectShowInfo (red-gleam boss tells). Holds DPS up to ~74%, pauses attack inputs right before
/// impact (~74-82%), and fires a crisp directional Rush (dodge) pulse right before explosion/hit (~82%+),
/// without continuously walking around.
/// </summary>
public sealed class CombatEvadeService : IDisposable
{
    private const float EscapeMarginMeters = 0.85f;
    private const float StaleHazardSec = 0.28f;
    private const float ClearHysteresisSec = 0.08f;
    private const float DodgeBurstDurationSec = 0.15f;

    private sealed class ActiveHazard
    {
        public int Key;
        public long SourceUuid;
        public int ShapeKind; // 0 = Box, 1 = Circle/Sector/Ring
        public Vector3 Center;
        public Vector3 Forward;
        public float HalfWidth;
        public float HalfLength;
        public float OuterRadius;
        public float InnerRadius;
        public float SectorAngleDeg;
        public float Progress01;
        public float ClipDuration;
        public double FirstSeenTime;
        public double LastSeenTime;
    }

    private sealed class TimedDodgeWindow
    {
        public int Key;
        public double PreHoldAt;
        public double TriggerAt;
        public double ExpireAt;
        public Vector3 SourcePos;
        public float MaxDist;
    }

    private static CombatEvadeService? _active;
    private readonly object _sync = new();

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly LuaBridge _lua;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly ZPhysicsSensor _sensor;
    private readonly Func<IBehavior?> _getCurrentBehavior;

    private Harmony? _harmony;
    private bool _hooksAttempted;
    private bool _hooksInstalled;

    private readonly Dictionary<int, ActiveHazard> _telegraphs = new();
    private readonly List<TimedDodgeWindow> _dodgeWindows = new();
    private readonly HashSet<int> _dodgedHazards = new();
    private readonly HashSet<int> _triggeredUiWarns = new();

    // Reflection caches populated lazily when hooks install or first fire
    private bool _warningMetaResolved;
    private PropertyInfo? _piShowIsStopped;
    private PropertyInfo? _piShowHost;
    private PropertyInfo? _piShowClipDuration;
    private PropertyInfo? _piShowClipTime;
    private PropertyInfo? _piShowClipProgress;
    private PropertyInfo? _piWarnCurData;
    private PropertyInfo? _piWarnDataShare2;
    private PropertyInfo? _piShareWarningType;
    private PropertyInfo? _piShareSectorRange;
    private PropertyInfo? _piShareCircleRange;
    private PropertyInfo? _piShareWarningColor;
    private PropertyInfo? _piShareOverlayColor;
    private PropertyInfo? _piShareOpenArrow;
    private PropertyInfo? _piWarnRuntimeList;
    private PropertyInfo? _piWarnScale;
    private PropertyInfo? _piWarnOffset;
    private PropertyInfo? _piWarnFromUuid;
    private PropertyInfo? _piListCount;
    private PropertyInfo? _piListItem;
    private MemberInfo? _miRtIsPlaying;
    private MemberInfo? _miRtTargetPos;
    private MemberInfo? _miRtTargetId;
    private MemberInfo? _miRtOverlayPct;

    private bool _uiWarnMetaResolved;
    private PropertyInfo? _piUiCurData;
    private PropertyInfo? _piUiShare;
    private PropertyInfo? _piUiBaseShare;
    private PropertyInfo? _piUiBaseDur;
    private PropertyInfo? _piUiDistCheck;
    private PropertyInfo? _piUiSkillTime;
    private PropertyInfo? _piUiLuaTriggered;

    private bool _effectMetaResolved;
    private PropertyInfo? _piEffCurData;
    private PropertyInfo? _piEffFxPath;

    private bool _entityMetaResolved;
    private PropertyInfo? _piEntMgrInstance;
    private PropertyInfo? _piEntMgrPlayerUuid;
    private PropertyInfo? _piEntMgrPlayerEnt;
    private MethodInfo? _miEntGetLuaIsInCombat;
    private PropertyInfo? _piEntUuid;
    private PropertyInfo? _piEntCharId;
    private PropertyInfo? _piEntIsPlayer;
    private PropertyInfo? _piEntIsBoss;
    private PropertyInfo? _piEntIsElite;
    private PropertyInfo? _piEntModel;
    private PropertyInfo? _piModelGoComp;
    private PropertyInfo? _piGoPos;
    private PropertyInfo? _piGoFwd;

    private float _sharePollTimer;
    private bool _shareSuppressed;
    private int _lastObservedLuaFlashSeq = -1;
    private double _lastRedFlashQueuedAt;
    private float _dodgeCooldown;
    private float _dodgeBurstTimer;
    private float _dodgeRetryTimer;
    private Vector3 _dodgeBurstDir;
    private bool _evadeDriving;

    public CombatEvadeService(
        IPluginServices services,
        BridgeConfig config,
        LuaBridge lua,
        AutoCombatKeeper autoCombat,
        PlayerMoveDriver moveDriver,
        LocomotionBoostController boost,
        ZPhysicsSensor sensor,
        Func<IBehavior?> getCurrentBehavior)
    {
        _services = services;
        _config = config;
        _lua = lua;
        _autoCombat = autoCombat;
        _moveDriver = moveDriver;
        _boost = boost;
        _sensor = sensor;
        _getCurrentBehavior = getCurrentBehavior;
        _active = this;
    }

    private bool IsCollectionNeeded =>
        _config.EnableAutoEvade
        && _autoCombat.DesiredEnabled
        && !_config.IsAdminLocked(FeaturePolicyKeys.AutoCombat)
        && _services.ClientState.Phase == GamePhase.World
        && ReadLiveInCombat();

    public void Tick(float dt)
    {
        if (_dodgeCooldown > 0f) _dodgeCooldown -= dt;

        // Tick boost release timers when standing outside an active movement behavior
        if (_getCurrentBehavior() == null)
        {
            _boost.TickReleasesOnly(dt);
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            ClearAllHazards();
            EndEvade();
            return;
        }

        if (!_hooksAttempted)
        {
            _hooksAttempted = true;
            InstallHooks();
        }

        bool inCombat = ReadLiveInCombat();
        bool activeCondition = _config.EnableAutoEvade
            && _autoCombat.DesiredEnabled
            && !_config.IsAdminLocked(FeaturePolicyKeys.AutoCombat)
            && _services.PlayerState.Health > 0
            && inCombat
            && IsCompatibleWithBehavior();

        if (!activeCondition)
        {
            if (!inCombat) ClearAllHazards();
            _shareSuppressed = false;
            EndEvade();
            return;
        }

        double now = Environment.TickCount64 * 0.001;
        PruneStaleHazards(now);

        var sp = _services.PlayerState.Position;
        var localPos = new Vector3(sp.X, sp.Y, sp.Z);

        // Poll Share/Stack alerts ("분담", "[집합]", "합류") + Lua ShowSkillWarning red-flash counter
        _sharePollTimer -= dt;
        if (_sharePollTimer <= 0f)
        {
            _sharePollTimer = 0.15f;
            PollShareAndRedFlashLua(now, localPos);
        }

        // Ongoing brief directional dodge-roll burst (only 0.15s — never walks continuously)
        if (_dodgeBurstTimer > 0f)
        {
            _dodgeBurstTimer -= dt;
            if (_dodgeRetryTimer > 0f)
            {
                _dodgeRetryTimer -= dt;
                if (_dodgeRetryTimer <= 0f)
                    _boost.TriggerDodgePulse(0.06f);
            }

            if (_dodgeBurstTimer > 0f)
            {
                _moveDriver.TryDrive(this, localPos, localPos + _dodgeBurstDir * 3.0f, 1.0f);
                return;
            }

            EndEvade();
            return;
        }

        if (_shareSuppressed)
        {
            EndEvade();
            return;
        }

        // 1. Evaluate timed Boss Red-Flash / UI Skill Warnings first
        bool preHold = false;
        if (EvaluateTimedDodgeWindows(now, localPos, out Vector3 uiDodgeDir))
        {
            StartJustDodgeBurst(localPos, uiDodgeDir);
            return;
        }
        preHold |= HasImminentTimedPreHold(now, localPos);

        // 2. Evaluate floor AoE telegraphs in Just-Dodge mode:
        //    Hold DPS until ~74%, pause attack inputs briefly (~74-82%), then Rush-dodge at ~82%+
        if (EvaluateFloorAoEJustDodge(now, localPos, out Vector3 aoeEscapeDir, out List<int> fireKeys, out bool aoePreHold))
        {
            lock (_sync)
            {
                foreach (int k in fireKeys)
                    _dodgedHazards.Add(k);
            }

            if (_dodgeCooldown <= 0f)
            {
                StartJustDodgeBurst(localPos, aoeEscapeDir);
                return;
            }
        }
        preHold |= aoePreHold;

        _autoCombat.SuppressForEvade = preHold;
        if (_evadeDriving)
        {
            _evadeDriving = false;
            _moveDriver.Uninstall(this);
        }
    }

    private void StartJustDodgeBurst(Vector3 localPos, Vector3 desiredDir)
    {
        _dodgeBurstDir = PickSafePhysicsDirection(localPos, desiredDir);
        _dodgeBurstTimer = DodgeBurstDurationSec;
        _dodgeRetryTimer = 0.06f;
        _dodgeCooldown = 1.10f;

        if (!_evadeDriving)
        {
            _moveDriver.Install(this);
            _evadeDriving = true;
        }
        _autoCombat.SuppressForEvade = true;
        _moveDriver.TryDrive(this, localPos, localPos + _dodgeBurstDir * 3.0f, 1.0f);
        _boost.TriggerDodgePulse(0.07f);
    }

    private bool IsCompatibleWithBehavior()
    {
        var cur = _getCurrentBehavior();
        if (cur == null) return true;
        if (cur is FollowPartyBehavior)
        {
            // Never fight FollowPartyBehavior when it owns the stick for catchup;
            // only evade when FollowPartyBehavior is holding in range or we already own the stick
            return !_moveDriver.IsDriving || _evadeDriving;
        }
        return false;
    }

    private void EndEvade()
    {
        _dodgeBurstTimer = 0f;
        _dodgeRetryTimer = 0f;
        _autoCombat.SuppressForEvade = false;
        if (_evadeDriving)
        {
            _evadeDriving = false;
            _moveDriver.Uninstall(this);
        }
    }

    private void ClearAllHazards()
    {
        lock (_sync)
        {
            _telegraphs.Clear();
            _dodgeWindows.Clear();
            _dodgedHazards.Clear();
            _triggeredUiWarns.Clear();
        }
    }

    private void PruneStaleHazards(double now)
    {
        lock (_sync)
        {
            PruneDict(_telegraphs, now, _dodgedHazards);
            for (int i = _dodgeWindows.Count - 1; i >= 0; i--)
            {
                if (now > _dodgeWindows[i].ExpireAt)
                    _dodgeWindows.RemoveAt(i);
            }
        }
    }

    private static void PruneDict(
        Dictionary<int, ActiveHazard> dict,
        double now,
        HashSet<int> dodged)
    {
        if (dict.Count == 0) return;
        List<int>? dead = null;
        foreach (var kv in dict)
        {
            if (now - kv.Value.LastSeenTime > StaleHazardSec)
            {
                dead ??= new List<int>();
                dead.Add(kv.Key);
            }
        }
        if (dead != null)
        {
            foreach (int k in dead)
            {
                dict.Remove(k);
                dodged.Remove(k);
            }
        }
    }

    private bool EvaluateFloorAoEJustDodge(
        double now,
        Vector3 localPos,
        out Vector3 escapeDir,
        out List<int> triggerKeys,
        out bool preHold)
    {
        escapeDir = Vector3.zero;
        triggerKeys = new List<int>();
        preHold = false;

        Vector3 accum = Vector3.zero;
        int hitCount = 0;

        lock (_sync)
        {
            foreach (var h in _telegraphs.Values)
            {
                if (_dodgedHazards.Contains(h.Key))
                    continue;
                if (MathF.Abs(localPos.y - h.Center.y) > 4.5f)
                    continue;
                if (!TryEvaluateSingleHazard(localPos, h, out Vector3 dir, out float needDist))
                    continue;

                double age = Math.Max(0.0, now - h.FirstSeenTime);
                float timeProg = h.ClipDuration > 0.25f ? (float)(age / h.ClipDuration) : (float)(age / 1.05);
                float effProg = Math.Max(h.Progress01, Math.Clamp(timeProg, 0f, 1f));
                float estRemainSec = h.ClipDuration > 0.25f
                    ? MathF.Max(0f, h.ClipDuration - (float)age)
                    : MathF.Max(0f, (1f - effProg) * 1.05f);

                if (effProg >= 0.74f || estRemainSec <= 0.30f)
                {
                    preHold = true;
                }

                if (effProg >= 0.82f || estRemainSec <= 0.19f)
                {
                    accum += dir * Math.Clamp(needDist, 0.5f, 8.0f);
                    hitCount++;
                    triggerKeys.Add(h.Key);
                }
            }
        }

        if (hitCount == 0 || accum.sqrMagnitude < 1e-6f)
            return false;

        accum.y = 0f;
        escapeDir = accum.normalized;
        return true;
    }

    internal static bool TryEvaluateSingleHazard(
        Vector3 localPos,
        int shapeKind,
        Vector3 center,
        Vector3 forward,
        float halfW,
        float halfL,
        float outerR,
        float innerR,
        float sectorDeg,
        out Vector3 escapeDir,
        out float distToSafety)
    {
        escapeDir = Vector3.zero;
        distToSafety = 0f;

        Vector3 fwd = forward;
        fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        Vector3 right = new(fwd.z, 0f, -fwd.x);

        Vector3 rel = localPos - center;
        rel.y = 0f;

        if (shapeKind == 0)
        {
            float wLim = Math.Clamp(halfW, 0.7f, 20f) + EscapeMarginMeters;
            float lLim = Math.Clamp(halfL, 0.7f, 32f) + EscapeMarginMeters;

            float lx = Vector3.Dot(rel, right);
            float lz = Vector3.Dot(rel, fwd);
            if (MathF.Abs(lx) >= wLim || MathF.Abs(lz) >= lLim)
                return false;

            float sideExit = wLim - MathF.Abs(lx);
            float lenExit = lLim - MathF.Abs(lz);
            if (sideExit <= lenExit * 1.35f)
            {
                escapeDir = lx >= 0f ? right : -right;
                distToSafety = sideExit;
            }
            else
            {
                escapeDir = lz >= 0f ? fwd : -fwd;
                distToSafety = lenExit;
            }
            return true;
        }

        // Circle / Donut Ring / Sector
        float effOuter = Math.Clamp(outerR, 1.0f, 30f) + EscapeMarginMeters;
        float dist = rel.magnitude;
        if (dist >= effOuter)
            return false;

        // Ring / Donut safe center check
        if (innerR >= 2.2f)
        {
            float effInner = MathF.Max(0.2f, innerR - EscapeMarginMeters);
            if (dist <= effInner)
                return false;

            if (dist < (innerR + outerR) * 0.5f && dist > 0.1f)
            {
                escapeDir = (-rel).normalized;
                distToSafety = MathF.Max(0.3f, dist - effInner);
            }
            else
            {
                escapeDir = dist > 0.1f ? (rel / dist) : right;
                distToSafety = effOuter - dist;
            }
            return true;
        }

        // Sector cone check
        if (sectorDeg > 1f && sectorDeg < 355f)
        {
            if (dist < 0.25f)
            {
                escapeDir = right;
                distToSafety = 1.2f;
                return true;
            }

            Vector3 relDir = rel / dist;
            float dot = Math.Clamp(Vector3.Dot(fwd, relDir), -1f, 1f);
            float angDeg = MathF.Acos(dot) * (180f / MathF.PI);
            float halfAng = sectorDeg * 0.5f + 7f;
            if (angDeg > halfAng)
                return false;

            float outCost = effOuter - dist;
            float sideSign = Vector3.Dot(rel, right) >= 0f ? 1f : -1f;
            float radMargin = MathF.Max(0.2f, dist * MathF.Sin((halfAng - angDeg) * (MathF.PI / 180f)) + EscapeMarginMeters);
            if (radMargin < outCost * 1.2f)
            {
                escapeDir = new Vector3(relDir.z * sideSign, 0f, -relDir.x * sideSign);
                distToSafety = radMargin;
            }
            else
            {
                escapeDir = relDir;
                distToSafety = outCost;
            }
            return true;
        }

        // Standard filled circle
        escapeDir = dist > 0.15f ? (rel / dist) : right;
        distToSafety = effOuter - dist;
        return true;
    }

    private static bool TryEvaluateSingleHazard(Vector3 localPos, ActiveHazard h, out Vector3 escapeDir, out float distToSafety)
        => TryEvaluateSingleHazard(
            localPos,
            h.ShapeKind,
            h.Center,
            h.Forward,
            h.HalfWidth,
            h.HalfLength,
            h.OuterRadius,
            h.InnerRadius,
            h.SectorAngleDeg,
            out escapeDir,
            out distToSafety);

    private Vector3 PickSafePhysicsDirection(Vector3 localPos, Vector3 desiredDir)
    {
        if (!_sensor.IsReady)
            return desiredDir;

        float[] offsets = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 180f };
        Vector3 eye = new(localPos.x, localPos.y + 0.65f, localPos.z);
        bool grounded = _sensor.HasGroundNear(localPos);

        foreach (float ang in offsets)
        {
            Vector3 cand = RotateY(desiredDir, ang);
            if (!_sensor.IsBlocked(eye, eye + cand * 2.5f, 0.42f, ignoreStep: true)
                && (!grounded || _sensor.HasGroundNear(localPos + cand * 2.2f)))
            {
                return cand;
            }
        }

        return desiredDir;
    }

    private bool EvaluateTimedDodgeWindows(double now, Vector3 localPos, out Vector3 dodgeDir)
    {
        dodgeDir = Vector3.right;
        if (_dodgeCooldown > 0f) return false;

        lock (_sync)
        {
            for (int i = _dodgeWindows.Count - 1; i >= 0; i--)
            {
                var w = _dodgeWindows[i];
                if (now < w.TriggerAt) continue;

                Vector3 away = localPos - w.SourcePos;
                away.y = 0f;
                float dist = away.magnitude;
                _dodgeWindows.RemoveAt(i);
                if (dist <= w.MaxDist)
                {
                    Vector3 baseDir = away.sqrMagnitude > 0.04f ? (away / dist) : Vector3.forward;
                    dodgeDir = RotateY(baseDir, 75f);
                    return true;
                }
            }
        }
        return false;
    }

    private bool HasImminentTimedPreHold(double now, Vector3 localPos)
    {
        lock (_sync)
        {
            foreach (var w in _dodgeWindows)
            {
                if (now >= w.PreHoldAt && now < w.TriggerAt)
                {
                    float dx = localPos.x - w.SourcePos.x;
                    float dz = localPos.z - w.SourcePos.z;
                    if (dx * dx + dz * dz <= w.MaxDist * w.MaxDist)
                        return true;
                }
            }
        }
        return false;
    }

    private void PollShareAndRedFlashLua(double now, Vector3 localPos)
    {
        if (!_lua.IsReady)
        {
            _shareSuppressed = false;
            return;
        }
        var res = _lua.Eval(@"
if not rawget(_G, '__agyShareHookInstalled') and Z and Z.TipsVM then
  local mtm = Z.TableMgr and Z.TableMgr.GetTable('MessageTableMgr')
  local function inspectMsg(a, b, c)
    local hit = false
    for _, arg in ipairs({a, b, c}) do
      if type(arg) == 'number' and arg > 0 and mtm and mtm.GetRow then
        local okR, row = pcall(function() return mtm.GetRow(arg, true) end)
        if okR and row and row.Content then
          local txt = tostring(row.Content)
          if txt:find('분담', 1, true) or txt:find('[집합]', 1, true) or txt:find('합류하', 1, true) or txt:find('집결', 1, true) then
            hit = true
            break
          end
        end
      elseif type(arg) == 'string' then
        if arg:find('분담', 1, true) or arg:find('[집합]', 1, true) or arg:find('합류하', 1, true) then
          hit = true
          break
        end
      end
    end
    if hit then
      rawset(_G, '__agyShareUntil', os.clock() + 7.0)
    end
  end

  if type(Z.TipsVM.ShowTips) == 'function' then
    local origShow = Z.TipsVM.ShowTips
    Z.TipsVM.ShowTips = function(...)
      pcall(inspectMsg, ...)
      return origShow(...)
    end
  end
  if type(Z.TipsVM.OpenGameBroadcast) == 'function' then
    local origBc = Z.TipsVM.OpenGameBroadcast
    Z.TipsVM.OpenGameBroadcast = function(...)
      pcall(inspectMsg, ...)
      return origBc(...)
    end
  end
  rawset(_G, '__agyShareHookInstalled', true)
end

if not rawget(_G, '__agyFlashHookInstalled') and Z and Z.EventMgr and type(Z.EventMgr.Dispatch) == 'function' then
  local warnEvt = Z.ConstValue and Z.ConstValue.Battle and Z.ConstValue.Battle.ShowSkillWarning
  local origDisp = Z.EventMgr.Dispatch
  Z.EventMgr.Dispatch = function(mgr, evName, ...)
    pcall(function()
      if (warnEvt and evName == warnEvt) or (type(evName) == 'string' and evName:find('SkillWarning', 1, true)) then
        rawset(_G, '__agyRedFlashSeq', (tonumber(rawget(_G, '__agyRedFlashSeq')) or 0) + 1)
      end
    end)
    return origDisp(mgr, evName, ...)
  end
  rawset(_G, '__agyFlashHookInstalled', true)
end

return {
  share = os.clock() < (tonumber(rawget(_G, '__agyShareUntil') or 0) or 0),
  flashSeq = tonumber(rawget(_G, '__agyRedFlashSeq') or 0) or 0
}
");
        if (res["ok"]?.GetValue<bool>() == true && res["result"] is JsonObject obj)
        {
            _shareSuppressed = obj["share"]?.GetValue<bool>() == true;
            int seq = obj["flashSeq"]?.GetValue<int>() ?? 0;
            if (_lastObservedLuaFlashSeq < 0)
            {
                _lastObservedLuaFlashSeq = seq;
            }
            else if (seq > _lastObservedLuaFlashSeq)
            {
                _lastObservedLuaFlashSeq = seq;
                QueueRedFlashWindow(0, now, localPos, 30.0f, 0.36f);
            }
        }
    }

    private void QueueRedFlashWindow(int key, double now, Vector3 sourcePos, float maxDist, float windupSec, bool replaceSameKey = false)
    {
        double preHoldAt = now + Math.Max(0.0, windupSec - 0.28);
        double triggerAt = now + Math.Max(0.06, windupSec - 0.15);
        lock (_sync)
        {
            if (replaceSameKey && key != 0)
            {
                for (int i = 0; i < _dodgeWindows.Count; i++)
                {
                    if (_dodgeWindows[i].Key == key)
                    {
                        _dodgeWindows[i].PreHoldAt = preHoldAt;
                        _dodgeWindows[i].TriggerAt = triggerAt;
                        _dodgeWindows[i].ExpireAt = triggerAt + 0.35;
                        _dodgeWindows[i].SourcePos = sourcePos;
                        _dodgeWindows[i].MaxDist = maxDist;
                        _lastRedFlashQueuedAt = now;
                        return;
                    }
                }
            }

            if (now - _lastRedFlashQueuedAt < 0.45)
                return;

            _lastRedFlashQueuedAt = now;
            _dodgeWindows.Add(new TimedDodgeWindow
            {
                Key = key,
                PreHoldAt = preHoldAt,
                TriggerAt = triggerAt,
                ExpireAt = triggerAt + 0.35,
                SourcePos = sourcePos,
                MaxDist = maxDist
            });
        }
    }

    // ───────── Harmony Hook Registration & Callbacks ─────────

    private void InstallHooks()
    {
        if (_hooksInstalled) return;
        try
        {
            _harmony ??= _services.Harmony.Create("agybridge.combatevade");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            int patched = 0;

            var warnType = InteropResolver.FindType("Panda.ZGame.ZWarningEffectShowInfo");
            if (warnType != null)
            {
                var miUpd = warnType.GetMethod("OnUpdate", flags, null, new[] { typeof(float) }, null);
                var miStop = warnType.GetMethod("OnStop", flags, null, Type.EmptyTypes, null);
                var miRec = warnType.GetMethod("OnRecycle", flags, null, Type.EmptyTypes, null);
                if (miUpd != null)
                {
                    _harmony.Patch(miUpd, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnWarningUpdatePostfix)));
                    patched++;
                }
                if (miStop != null)
                    _harmony.Patch(miStop, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnWarningStopPostfix)));
                if (miRec != null)
                    _harmony.Patch(miRec, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnWarningStopPostfix)));
            }

            var uiWarnType = InteropResolver.FindType("Panda.ZGame.ZUISkillWarningShowInfo");
            if (uiWarnType != null)
            {
                var miShow = uiWarnType.GetMethod("OnShow", flags, null, Type.EmptyTypes, null);
                var miChk = uiWarnType.GetMethod("checkEventTrigger", flags, null, Type.EmptyTypes, null);
                var miUiStop = uiWarnType.GetMethod("OnStop", flags, null, Type.EmptyTypes, null);
                var miUiRec = uiWarnType.GetMethod("OnRecycle", flags, null, Type.EmptyTypes, null);
                if (miShow != null)
                {
                    _harmony.Patch(miShow, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnUiWarningShowPostfix)));
                    patched++;
                }
                if (miChk != null)
                {
                    _harmony.Patch(miChk, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnUiWarningCheckPostfix)));
                    patched++;
                }
                if (miUiStop != null)
                    _harmony.Patch(miUiStop, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnUiWarningStopPostfix)));
                if (miUiRec != null)
                    _harmony.Patch(miUiRec, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnUiWarningStopPostfix)));
            }

            var effType = InteropResolver.FindType("Panda.ZGame.ZEffectShowInfo");
            if (effType != null)
            {
                var miEffShow = effType.GetMethod("OnShow", flags, null, Type.EmptyTypes, null);
                if (miEffShow != null)
                {
                    _harmony.Patch(miEffShow, postfix: new HarmonyMethod(typeof(CombatEvadeService), nameof(OnEffectShowPostfix)));
                    patched++;
                }
            }

            _hooksInstalled = patched > 0;
            _services.Log.Info($"[AgyBridge][Evade] Installed {patched} Harmony hook groups.");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Evade] InstallHooks error: {ex.Message}");
        }
    }

    private static void OnWarningUpdatePostfix(object __instance)
    {
        var svc = _active;
        if (svc == null || !svc.IsCollectionNeeded) return;
        try { svc.CaptureWarning(__instance); } catch { }
    }

    private static void OnWarningStopPostfix(object __instance)
    {
        var svc = _active;
        if (svc == null) return;
        int key = RuntimeHelpers.GetHashCode(__instance);
        lock (svc._sync)
        {
            svc._telegraphs.Remove(key);
            svc._dodgedHazards.Remove(key);
        }
    }

    private static void OnUiWarningShowPostfix(object __instance)
    {
        var svc = _active;
        if (svc == null || !svc.IsCollectionNeeded) return;
        try { svc.CaptureUiWarning(__instance, fromTriggerCheck: false); } catch { }
    }

    private static void OnUiWarningCheckPostfix(object __instance)
    {
        var svc = _active;
        if (svc == null || !svc.IsCollectionNeeded) return;
        try { svc.CaptureUiWarning(__instance, fromTriggerCheck: true); } catch { }
    }

    private static void OnUiWarningStopPostfix(object __instance)
    {
        var svc = _active;
        if (svc == null) return;
        int key = RuntimeHelpers.GetHashCode(__instance);
        lock (svc._sync)
        {
            svc._triggeredUiWarns.Remove(key);
        }
    }

    private static void OnEffectShowPostfix(object __instance)
    {
        var svc = _active;
        if (svc == null || !svc.IsCollectionNeeded) return;
        try { svc.CaptureEffectRedFlash(__instance); } catch { }
    }

    private void CaptureWarning(object inst)
    {
        EnsureWarningMeta(inst);
        int key = RuntimeHelpers.GetHashCode(inst);
        lock (_sync)
        {
            if (_dodgedHazards.Contains(key))
                return;
        }

        if (_piShowIsStopped?.GetValue(inst) is bool stopped && stopped)
        {
            lock (_sync)
            {
                _telegraphs.Remove(key);
                _dodgedHazards.Remove(key);
            }
            return;
        }

        object? curData = _piWarnCurData?.GetValue(inst);
        object? share2 = curData == null ? null : _piWarnDataShare2?.GetValue(curData);
        if (share2 == null) return;

        // Exclude green/friendly healing or safe-zone markers
        if (_piShareWarningColor?.GetValue(share2) is Color wc && IsFriendlyGreen(wc)) return;
        if (_piShareOverlayColor?.GetValue(share2) is Color oc && IsFriendlyGreen(oc)) return;

        int warnType = _piShareWarningType?.GetValue(share2) is int wt ? wt : 1;
        float sectorDeg = _piShareSectorRange?.GetValue(share2) is float sr ? sr : 0f;
        float circleInner = _piShareCircleRange?.GetValue(share2) is float cr ? cr : 0f;
        _ = _piShareOpenArrow;

        Vector3 scale = _piWarnScale?.GetValue(inst) is Vector3 sv ? sv : new Vector3(4f, 1f, 4f);
        Vector3 offset = _piWarnOffset?.GetValue(inst) is Vector3 ov ? ov : Vector3.zero;
        long fromUuid = _piWarnFromUuid?.GetValue(inst) is long fu ? fu : 0L;

        object? hostObj = _piShowHost?.GetValue(inst);
        bool isBossOrElite = hostObj != null
            && ((_piEntIsBoss?.GetValue(hostObj) is bool ib && ib)
                || (_piEntIsElite?.GetValue(hostObj) is bool ie && ie));

        float clipDur = _piShowClipDuration?.GetValue(inst) is float cd ? cd : 0f;
        if (clipDur > 0.01f && clipDur < 0.45f)
        {
            lock (_sync) _telegraphs.Remove(key);
            return;
        }

        float sx = MathF.Abs(scale.x);
        float sz = MathF.Abs(scale.z);
        if (!isBossOrElite && MathF.Max(sx, sz) < 3.0f)
        {
            lock (_sync) _telegraphs.Remove(key);
            return;
        }

        Vector3 hostPos = Vector3.zero;
        Vector3 hostFwd = Vector3.forward;
        bool hasHostTransform = TryReadHostTransform(hostObj, out hostPos, out hostFwd);

        long localUuid = GetLocalPlayerUuid();
        Vector3 rtPos = hostPos;
        long rtTargetId = 0L;
        float overlayPct = 0f;
        bool hasRt = false;

        object? rtList = _piWarnRuntimeList?.GetValue(inst);
        if (rtList != null && _piListCount != null && _piListItem != null)
        {
            int cnt = _piListCount.GetValue(rtList) is int c ? c : 0;
            for (int i = 0; i < cnt; i++)
            {
                object? item = _piListItem.GetValue(rtList, new object[] { i });
                if (item == null) continue;
                bool playing = ReadBool(item, _miRtIsPlaying, true);
                if (!playing) continue;

                rtPos = ReadVec3(item, _miRtTargetPos, hostPos);
                rtTargetId = ReadLong(item, _miRtTargetId, 0L);
                overlayPct = MathF.Max(overlayPct, ReadFloat(item, _miRtOverlayPct, 0f));
                hasRt = true;
                break;
            }
        }

        // Any marker locked directly onto our player (normal attack target lock or shared-damage chase marker)
        // moves WITH us — evading it would run forever. Ignore unconditionally.
        if (rtTargetId != 0L && (rtTargetId == localUuid || rtTargetId == _services.PlayerState.CharId))
        {
            lock (_sync) _telegraphs.Remove(key);
            return;
        }

        Vector3 center = (hasRt && rtPos.sqrMagnitude > 0.01f) ? rtPos : hostPos;
        if (!hasRt && !hasHostTransform) return;

        if (offset.sqrMagnitude > 0.001f)
        {
            Vector3 fwd = hostFwd; fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f) fwd.Normalize(); else fwd = Vector3.forward;
            Vector3 right = new(fwd.z, 0f, -fwd.x);
            center += right * offset.x + new Vector3(0f, offset.y, 0f) + fwd * offset.z;
        }

        float clipProgress = _piShowClipProgress?.GetValue(inst) is float cp ? cp : 0f;
        double now = Environment.TickCount64 * 0.001;

        lock (_sync)
        {
            double firstSeen = _telegraphs.TryGetValue(key, out var existing) ? existing.FirstSeenTime : now;
            _telegraphs[key] = new ActiveHazard
            {
                Key = key,
                SourceUuid = fromUuid,
                ShapeKind = warnType == 0 ? 0 : 1,
                Center = center,
                Forward = hostFwd,
                HalfWidth = MathF.Max(0.8f, sx * 0.5f),
                HalfLength = MathF.Max(0.8f, sz * 0.5f),
                OuterRadius = MathF.Max(1.0f, MathF.Max(sx, sz)),
                InnerRadius = circleInner,
                SectorAngleDeg = sectorDeg,
                Progress01 = Math.Clamp(MathF.Max(overlayPct, clipProgress), 0f, 1f),
                ClipDuration = clipDur,
                FirstSeenTime = firstSeen,
                LastSeenTime = now
            };
        }
    }

    private void CaptureUiWarning(object inst, bool fromTriggerCheck)
    {
        EnsureUiWarnMeta(inst);
        int key = RuntimeHelpers.GetHashCode(inst);

        if (fromTriggerCheck)
        {
            if (_piUiLuaTriggered?.GetValue(inst) is not bool trig || !trig)
                return;
            lock (_sync)
            {
                if (!_triggeredUiWarns.Add(key))
                    return;
            }
        }

        object? host = _piShowHost?.GetValue(inst);
        if (IsFriendlyPlayerHost(host)) return;

        object? curData = _piUiCurData?.GetValue(inst);
        object? share = curData == null ? null : _piUiShare?.GetValue(curData);
        if (share == null) return;

        float skillTime = _piUiSkillTime?.GetValue(share) is float st ? st : 0f;
        if (skillTime > 15f) skillTime *= 0.001f;
        float distCheck = _piUiDistCheck?.GetValue(share) is float dc ? dc : 0f;
        float clipDur = _piShowClipDuration?.GetValue(inst) is float cd ? cd : 0f;
        float clipTime = _piShowClipTime?.GetValue(inst) is float ct ? ct : 0f;
        object? baseShare = _piUiBaseShare?.GetValue(curData);
        float baseDur = baseShare != null && _piUiBaseDur?.GetValue(baseShare) is float bd ? bd : 0f;
        if (baseDur > 15f) baseDur *= 0.001f;

        float windupSec;
        if (fromTriggerCheck)
        {
            if (skillTime > clipTime + 0.08f) windupSec = skillTime - clipTime;
            else if (clipDur > clipTime + 0.08f) windupSec = clipDur - clipTime;
            else if (baseDur > clipTime + 0.08f) windupSec = baseDur - clipTime;
            else windupSec = 0.36f;
            windupSec = Math.Clamp(windupSec, 0.16f, 2.5f);
        }
        else
        {
            windupSec = skillTime > 0.08f ? skillTime : (clipDur > 0.08f ? clipDur : (baseDur > 0.08f ? baseDur : 0.45f));
            windupSec = Math.Clamp(windupSec, 0.20f, 3.5f);
        }

        Vector3 hostPos = Vector3.zero;
        if (!TryReadHostTransform(host, out hostPos, out _))
        {
            var sp = _services.PlayerState.Position;
            hostPos = new Vector3(sp.X, sp.Y, sp.Z);
        }

        double now = Environment.TickCount64 * 0.001;
        float maxDist = distCheck > 0.5f ? distCheck + 3.0f : 22.0f;
        QueueRedFlashWindow(key, now, hostPos, maxDist, windupSec, replaceSameKey: fromTriggerCheck);
    }

    private void CaptureEffectRedFlash(object inst)
    {
        EnsureEffectMeta(inst);
        object? host = _piShowHost?.GetValue(inst);
        if (host == null || IsFriendlyPlayerHost(host)) return;
        bool isBossOrElite = (_piEntIsBoss?.GetValue(host) is bool ib && ib)
            || (_piEntIsElite?.GetValue(host) is bool ie && ie);
        if (!isBossOrElite) return;

        object? curData = _piEffCurData?.GetValue(inst);
        string? fxpath = curData == null ? null : _piEffFxPath?.GetValue(curData) as string;
        if (string.IsNullOrEmpty(fxpath)) return;

        bool isFlash = fxpath.Contains("skillwarning", StringComparison.OrdinalIgnoreCase)
            || fxpath.Contains("warn_red", StringComparison.OrdinalIgnoreCase)
            || fxpath.Contains("red_flash", StringComparison.OrdinalIgnoreCase)
            || fxpath.Contains("hongguang", StringComparison.OrdinalIgnoreCase);
        if (!isFlash) return;

        if (!TryReadHostTransform(host, out Vector3 hostPos, out _))
        {
            var sp = _services.PlayerState.Position;
            hostPos = new Vector3(sp.X, sp.Y, sp.Z);
        }
        QueueRedFlashWindow(RuntimeHelpers.GetHashCode(inst), Environment.TickCount64 * 0.001, hostPos, 22.0f, 0.36f);
    }

    private bool IsFriendlyPlayerHost(object? host)
    {
        if (host == null) return false;
        long uuid = _piEntUuid?.GetValue(host) is long u ? u : 0L;
        if (uuid > 0 && uuid == GetLocalPlayerUuid()) return true;
        if (_piEntIsPlayer?.GetValue(host) is bool ip && ip) return true;
        if (_piEntCharId?.GetValue(host) is long cid && cid > 0) return true;
        return false;
    }

    private void EnsureWarningMeta(object inst)
    {
        if (_warningMetaResolved) return;
        _warningMetaResolved = true;
        EnsureEntityMeta();

        var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = inst.GetType();
        _piShowIsStopped ??= t.GetProperty("IsStopped", f);
        _piShowHost ??= t.GetProperty("Host", f);
        _piShowClipDuration ??= t.GetProperty("ClipDurationTime", f);
        _piShowClipTime ??= t.GetProperty("ClipTime", f);
        _piShowClipProgress ??= t.GetProperty("ClipProgress", f);
        _piWarnCurData = t.GetProperty("CurData", f);
        _piWarnRuntimeList = t.GetProperty("runtimeData_", f);
        _piWarnScale = t.GetProperty("scale_", f);
        _piWarnOffset = t.GetProperty("offset_", f);
        _piWarnFromUuid = t.GetProperty("fromUuid_", f);

        var wdType = _piWarnCurData?.PropertyType ?? InteropResolver.FindType("Panda.ZGame.WarningEffectData");
        _piWarnDataShare2 = wdType?.GetProperty("Share2", f);
        var shareType = _piWarnDataShare2?.PropertyType ?? InteropResolver.FindType("Panda.ZGame.WarningEffectDataShare");
        if (shareType != null)
        {
            _piShareWarningType = shareType.GetProperty("WarningType", f);
            _piShareSectorRange = shareType.GetProperty("SectorRange", f);
            _piShareCircleRange = shareType.GetProperty("CircleRange", f);
            _piShareWarningColor = shareType.GetProperty("WarningColor", f);
            _piShareOverlayColor = shareType.GetProperty("OverlayColor", f);
            _piShareOpenArrow = shareType.GetProperty("OpenArrowEffect", f);
        }

        var zlistType = _piWarnRuntimeList?.PropertyType;
        _piListCount = zlistType?.GetProperty("Count", f);
        _piListItem = zlistType?.GetProperty("Item", f, null, null, new[] { typeof(int) }, null);

        var rtType = InteropResolver.FindType("Panda.ZGame.MultiWarningRuntimeData");
        if (rtType != null)
        {
            _miRtIsPlaying = (MemberInfo?)rtType.GetProperty("IsPlaying", f) ?? rtType.GetField("IsPlaying", f);
            _miRtTargetPos = (MemberInfo?)rtType.GetProperty("TargetPos", f) ?? rtType.GetField("TargetPos", f);
            _miRtTargetId = (MemberInfo?)rtType.GetProperty("TargetId", f) ?? rtType.GetField("TargetId", f);
            _miRtOverlayPct = (MemberInfo?)rtType.GetProperty("OverlayPercent", f) ?? rtType.GetField("OverlayPercent", f);
        }
    }

    private void EnsureUiWarnMeta(object inst)
    {
        if (_uiWarnMetaResolved) return;
        _uiWarnMetaResolved = true;
        EnsureEntityMeta();

        var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = inst.GetType();
        _piShowHost ??= t.GetProperty("Host", f);
        _piShowClipDuration ??= t.GetProperty("ClipDurationTime", f);
        _piShowClipTime ??= t.GetProperty("ClipTime", f);
        _piUiLuaTriggered = t.GetProperty("luaEventTriggered_", f);
        _piUiCurData = t.GetProperty("CurData", f);
        var infoType = _piUiCurData?.PropertyType ?? InteropResolver.FindType("Panda.ZGame.UISkillWarningInfo");
        _piUiShare = infoType?.GetProperty("Share", f);
        _piUiBaseShare = infoType?.GetProperty("BaseShare", f);
        var baseShareType = _piUiBaseShare?.PropertyType ?? InteropResolver.FindType("Panda.ZGame.ShowDataBaseShare");
        _piUiBaseDur = baseShareType?.GetProperty("duration", f);
        var shareType = _piUiShare?.PropertyType ?? InteropResolver.FindType("Panda.ZGame.UISkillWarningInfoShare");
        _piUiDistCheck = shareType?.GetProperty("DistanceCheckValue", f);
        _piUiSkillTime = shareType?.GetProperty("SkillTime", f);
    }

    private void EnsureEffectMeta(object inst)
    {
        if (_effectMetaResolved) return;
        _effectMetaResolved = true;
        EnsureEntityMeta();

        var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = inst.GetType();
        _piShowHost ??= t.GetProperty("Host", f);
        _piEffCurData = t.GetProperty("CurData", f);
        var edType = _piEffCurData?.PropertyType ?? InteropResolver.FindType("Panda.ZGame.EffectData");
        _piEffFxPath = edType?.GetProperty("fxpath", f);
    }

    private void EnsureEntityMeta()
    {
        if (_entityMetaResolved) return;
        _entityMetaResolved = true;

        var emType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
        _piEntMgrInstance = emType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        _piEntMgrPlayerUuid = emType?.GetProperty("PlayerUuid", BindingFlags.Public | BindingFlags.Instance);
        _piEntMgrPlayerEnt = emType?.GetProperty("PlayerEnt", BindingFlags.Public | BindingFlags.Instance)
            ?? emType?.GetProperty("PlayerEntity", BindingFlags.Public | BindingFlags.Instance);

        var f = BindingFlags.Public | BindingFlags.Instance;
        var entType = InteropResolver.FindType("Panda.ZGame.ZEntity");
        _miEntGetLuaIsInCombat = entType?.GetMethod("GetLuaIsInCombat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        _piEntUuid = entType?.GetProperty("Uuid", f);
        _piEntCharId = entType?.GetProperty("CharId", f);
        _piEntIsPlayer = entType?.GetProperty("IsPlayer", f);
        _piEntIsBoss = entType?.GetProperty("IsBoss", f);
        _piEntIsElite = entType?.GetProperty("IsElite", f);
        _piEntModel = entType?.GetProperty("Model", f);
        var modelType = InteropResolver.FindType("Panda.ZGame.ZModel");
        _piModelGoComp = modelType?.GetProperty("ModelGoComp", f);
        var goType = InteropResolver.FindType("Panda.ZGame.ModelGoCompBase");
        _piGoPos = goType?.GetProperty("Position", f);
        _piGoFwd = goType?.GetProperty("Forward", f);
    }

    private bool ReadLiveInCombat()
    {
        if (_services.ClientState.Phase != GamePhase.World || !_services.PlayerState.IsAvailable)
            return false;
        EnsureEntityMeta();
        try
        {
            var mgr = _piEntMgrInstance?.GetValue(null);
            var ent = mgr == null ? null : _piEntMgrPlayerEnt?.GetValue(mgr);
            if (ent != null)
            {
                _miEntGetLuaIsInCombat ??= ent.GetType().GetMethod("GetLuaIsInCombat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (_miEntGetLuaIsInCombat?.Invoke(ent, null) is bool b)
                    return b;
            }
        }
        catch { }
        return _services.CombatState.LocalPlayerInCombat;
    }

    private long GetLocalPlayerUuid()
    {
        try
        {
            var mgr = _piEntMgrInstance?.GetValue(null);
            if (mgr != null && _piEntMgrPlayerUuid?.GetValue(mgr) is long uid)
                return uid;
        }
        catch { }
        return 0L;
    }

    private bool TryReadHostTransform(object? host, out Vector3 pos, out Vector3 fwd)
    {
        pos = Vector3.zero;
        fwd = Vector3.forward;
        if (host == null) return false;
        try
        {
            object? model = _piEntModel?.GetValue(host);
            object? go = model == null ? null : _piModelGoComp?.GetValue(model);
            if (go == null) return false;
            if (_piGoPos?.GetValue(go) is Vector3 p)
            {
                pos = p;
                if (_piGoFwd?.GetValue(go) is Vector3 f) fwd = f;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsFriendlyGreen(Color c)
        => c.a > 0.05f && c.g > 0.35f && c.g > c.r * 1.15f;

    private static object? ReadMember(object obj, MemberInfo? mi) => mi switch
    {
        PropertyInfo pi => pi.GetValue(obj),
        FieldInfo fi => fi.GetValue(obj),
        _ => null
    };

    private static bool ReadBool(object obj, MemberInfo? mi, bool defVal)
        => ReadMember(obj, mi) is bool b ? b : defVal;

    private static long ReadLong(object obj, MemberInfo? mi, long defVal)
        => ReadMember(obj, mi) is long l ? l : defVal;

    private static float ReadFloat(object obj, MemberInfo? mi, float defVal)
        => ReadMember(obj, mi) is float f ? f : defVal;

    private static Vector3 ReadVec3(object obj, MemberInfo? mi, Vector3 defVal)
        => ReadMember(obj, mi) is Vector3 v ? v : defVal;

    private static Vector3 RotateY(Vector3 v, float degrees)
    {
        float rad = degrees * (MathF.PI / 180f);
        float c = MathF.Cos(rad);
        float s = MathF.Sin(rad);
        return new Vector3(v.x * c + v.z * s, 0f, -v.x * s + v.z * c).normalized;
    }

    public JsonObject GetStatusJson()
    {
        int tCount;
        lock (_sync)
        {
            tCount = _telegraphs.Count;
        }
        return new JsonObject
        {
            ["enabled"] = _config.EnableAutoEvade,
            ["hooks_installed"] = _hooksInstalled,
            ["evading"] = _evadeDriving,
            ["active_telegraphs"] = tCount,
            ["active_bullets"] = 0,
            ["share_suppressed"] = _shareSuppressed
        };
    }

    public void Dispose()
    {
        _active = null;
        EndEvade();
        ClearAllHazards();
        try { _harmony?.UnpatchSelf(); } catch { }
    }
}
