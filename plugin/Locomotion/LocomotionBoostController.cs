using System;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Movement;
using Stellar.AgyBridge.Core;
using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Non-blocking 3-tier locomotion accelerator layered on top of PlayerMoveDriver:
/// 1. Far range (>= MobilitySkillMinDistance): Casts 'E' / Phantom Dash (환영 대시) when off cooldown.
/// 2. Mid-far range (>= BunnyhopMinDistance): Chains Rush (Dash) -> Jump while direction stick is held (Bunnyhop).
/// 3. Close range (< BunnyhopMinDistance): Releases boost inputs and lets standard stick steering approach smoothly.
/// </summary>
public sealed class LocomotionBoostController
{
    private const int ActorStateSwim = 6;
    private const int ActorStateFlow = 16;
    private const int ActorStateGlide = 17;
    private const int MoveDash = 3;
    private const int MoveParkourRun = 11;

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly IMovementAdapter _movement;

    private bool _picResolved;
    private PropertyInfo? _piPicInstance;
    private MethodInfo? _miRush;
    private MethodInfo? _miJump;
    private MethodInfo? _miAttack;
    private MethodInfo? _miSwimSprint;
    private MethodInfo? _miGlideBool;
    private MethodInfo? _miFlowBool;
    private MethodInfo? _miResonance1Press;
    private MethodInfo? _miResonance1Release;
    private MethodInfo? _miResonance2Press;
    private MethodInfo? _miResonance2Release;
    private PropertyInfo? _piEmptyInputArgs;

    private float _driveWarmupTimer;
    private float _rushReleaseTimer;
    private float _jumpTriggerTimer;
    private float _jumpReleaseTimer;
    private int _activeAttackSlotRelease;
    private float _attackReleaseTimer;

    private float _mobilityCheckTimer;
    private float _postMobilityGraceTimer;
    private float _bunnyhopCooldownTimer;

    private bool _rushHeld;
    private bool _dodgePulseActive;
    private int _lastReleaseTickFrame = -1;
    private bool _jumpHeld;
    private string _activeBoostMode = "walk";

    // Stamina conservation & swim safety state
    private bool _conservingStamina;
    private float _lastStaminaRatio = 1f;
    private float _lastCurStamina;
    private float _lastMaxStamina;
    private float _prevRawStamina = -1f;
    private int _lastActorState;
    private int _lastVirtualMoveType;
    private float _sprintBreakPauseTimer;
    private float _sprintBreakCooldown;

    // Stuck & vertical elevation rapid-jump state
    private bool _hasProgressAnchor;
    private Vector3 _progressAnchorPos;
    private float _progressWindowTimer;
    private int _stuckCounter;
    private float _rapidJumpActiveTimer;
    private float _rapidJumpPulseCooldown;
    private float _gapDoubleJumpTimer;
    private float _sidestepFlipTimer;
    private float _sidestepSign = 1f;

    public SmartPathNavigator PathNavigator { get; }
    public string ActiveBoostMode => _activeBoostMode;
    public bool IsRapidJumping => _rapidJumpActiveTimer > 0f;
    public bool IsDodgePulseActive => _dodgePulseActive;
    public bool IsConservingStamina => _conservingStamina;
    public bool IsSwimming => _lastActorState == ActorStateSwim;
    public float LastStaminaRatio => _lastStaminaRatio;
    public float SuggestedSteerYawOffset => (!_config.EnableSmartPathfinding && _rapidJumpActiveTimer > 0f && _stuckCounter >= 4) ? (38f * _sidestepSign) : 0f;

    public LocomotionBoostController(IPluginServices services, BridgeConfig config, IMovementAdapter movement)
    {
        _services = services;
        _config = config;
        _movement = movement;
        PathNavigator = new SmartPathNavigator(_services, _config, new ZPhysicsSensor(_services));
    }

    public void Reset()
    {
        ResetBoostInputsOnly();
        _conservingStamina = false;
        _prevRawStamina = -1f;
        _sprintBreakPauseTimer = 0f;
        _sprintBreakCooldown = 0f;
        PathNavigator.Reset();
    }

    private void ResetBoostInputsOnly()
    {
        _driveWarmupTimer = 0f;
        _jumpTriggerTimer = 0f;
        _hasProgressAnchor = false;
        _progressWindowTimer = 0f;
        _stuckCounter = 0;
        _rapidJumpActiveTimer = 0f;
        _rapidJumpPulseCooldown = 0f;
        _gapDoubleJumpTimer = 0f;
        _sidestepFlipTimer = 0f;
        _activeBoostMode = "walk";

        if (!_dodgePulseActive)
        {
            if (_rushHeld)
            {
                InvokeRush(false);
                _rushHeld = false;
            }
            _rushReleaseTimer = 0f;
        }

        if (_jumpHeld)
        {
            InvokeJump(false);
            _jumpHeld = false;
        }
        _jumpReleaseTimer = 0f;

        if (_activeAttackSlotRelease > 0)
        {
            InvokeAttack(_activeAttackSlotRelease, false);
            _activeAttackSlotRelease = 0;
        }
        _attackReleaseTimer = 0f;
    }

    /// <summary>
    /// Computes 2-layer detour steering via SmartPathNavigator, coordinates jump/dash boost,
    /// and drives PlayerMoveDriver smoothly toward the obstacle-free waypoint.
    /// </summary>
    public string DriveWithSmartNav(
        object owner,
        PlayerMoveDriver moveDriver,
        float dt,
        int sceneId,
        Vector3 localPos,
        Vector3 goalPos,
        float distanceToGoal,
        float baseSpeedScale,
        bool hasTargetElevation = true,
        bool useBoost = true,
        long targetKey = 0L,
        bool allowLedgeClimb = false)
    {
        Vector3 steerTarget = PathNavigator.ComputeSteerTarget(
            dt,
            sceneId,
            localPos,
            goalPos,
            targetKey,
            allowLedgeClimb);

        float navDx = steerTarget.x - localPos.x;
        float navDz = steerTarget.z - localPos.z;
        float legHorizDist = MathF.Sqrt(navDx * navDx + navDz * navDz);
        bool hasCornerAhead = PathNavigator.HasIntermediateWaypoint;
        Vector3 boostTarget = hasCornerAhead ? steerTarget : goalPos;
        float boostDist = hasCornerAhead ? MathF.Min(distanceToGoal, legHorizDist) : distanceToGoal;

        if (useBoost)
        {
            TickWhileDriving(
                dt,
                localPos,
                boostTarget,
                boostDist,
                hasTargetElevation,
                PathNavigator.IsWallAhead,
                PathNavigator.IsClimbableStepAhead,
                PathNavigator.IsDetouring,
                PathNavigator.IsCliffAhead,
                PathNavigator.IsDropVaultAhead,
                PathNavigator.IsGapJumpAhead,
                PathNavigator.GapJumpDistance,
                PathNavigator.GapJumpHeightDelta);
        }
        else
        {
            EnsureResolved();
            TickPendingReleases(dt);
            UpdateStaminaAndState(dt);
            ResetBoostInputsOnly();
        }

        if (_sprintBreakPauseTimer > 0f)
        {
            moveDriver.Stop(owner);
            return _activeBoostMode;
        }

        if (navDx * navDx + navDz * navDz < 0.04f)
        {
            moveDriver.Stop(owner);
            return _activeBoostMode;
        }

        float scale = (IsRapidJumping
            || PathNavigator.IsGapJumpAhead
            || PathNavigator.IsAirborneCommitted
            || PathNavigator.IsDropVaultAhead)
            ? 1.0f
            : baseSpeedScale;
        moveDriver.TryDrive(owner, localPos, steerTarget, scale, SuggestedSteerYawOffset);
        return _activeBoostMode;
    }

    public bool TriggerDodgePulse(float holdSec = 0.07f)
    {
        EnsureResolved();
        if (_rushHeld) return false;
        if (!InvokeRush(true))
            return false;
        _rushHeld = true;
        _dodgePulseActive = true;
        _rushReleaseTimer = holdSec;
        return true;
    }

    public void TickReleasesOnly(float dt)
    {
        EnsureResolved();
        TickPendingReleases(dt);
    }

    /// <summary>
    /// Legacy horizontal-only overload delegating to 3D stuck + elevation aware TickWhileDriving.
    /// </summary>
    public string TickWhileDriving(float dt, float distanceToTarget)
    {
        var sp = _services.PlayerState.Position;
        var lp = new Vector3(sp.X, sp.Y, sp.Z);
        return TickWhileDriving(dt, lp, lp, distanceToTarget, hasTargetElevation: false);
    }

    /// <summary>
    /// Advances pending press/release timers, aggressively chain-jumps (Jump -> Double-Jump -> Wall-Vault)
    /// whenever horizontal progress stalls on a ledge/obstacle OR the target is vertically above localPos,
    /// and otherwise triggers Phantom Dash ('E') or Bunnyhop based on target distance.
    /// </summary>
    public string TickWhileDriving(
        float dt,
        Vector3 localPos,
        Vector3 targetPos,
        float distanceToTarget,
        bool hasTargetElevation = true,
        bool wallAhead = false,
        bool stepAhead = false,
        bool isDetouring = false,
        bool cliffAhead = false,
        bool dropVaultAhead = false,
        bool gapJumpAhead = false,
        float gapJumpDist = 0f,
        float gapJumpDy = 0f)
    {
        EnsureResolved();
        TickPendingReleases(dt);

        if (_mobilityCheckTimer > 0f) _mobilityCheckTimer -= dt;
        if (_postMobilityGraceTimer > 0f) _postMobilityGraceTimer -= dt;
        if (_bunnyhopCooldownTimer > 0f) _bunnyhopCooldownTimer -= dt;
        if (_rapidJumpPulseCooldown > 0f) _rapidJumpPulseCooldown -= dt;
        if (!gapJumpAhead && _gapDoubleJumpTimer > 0f)
            _gapDoubleJumpTimer = Math.Max(0f, _gapDoubleJumpTimer - dt);

        UpdateStaminaAndState(dt);

        // Priority Gate A: Stamina Conservation (<= 50% -> calm movement without Rush, Jump, or Mobility Skill)
        if (_conservingStamina)
        {
            CancelBoostForStaminaConserve();
            _activeBoostMode = $"conserve_stamina({_lastStaminaRatio * 100f:F0}%)";
            return _activeBoostMode;
        }

        // Priority Gate B: Swimming Safety (prevent rapid Jump / Rush / SwimSprint drowning loops in water)
        if (IsSwimming)
        {
            _jumpTriggerTimer = 0f;
            _rapidJumpActiveTimer = 0f;
            _gapDoubleJumpTimer = 0f;
            _stuckCounter = 0;
            _hasProgressAnchor = false;
            _progressWindowTimer = 0f;
            InvokeSwimSprint(false);

            if (stepAhead
                && _config.EnableObstacleJump
                && HasStaminaForBoost()
                && _rapidJumpPulseCooldown <= 0f
                && !_jumpHeld
                && InvokeJump(true))
            {
                _jumpHeld = true;
                _jumpReleaseTimer = 0.07f;
                _rapidJumpPulseCooldown = 0.85f;
                _activeBoostMode = "swim_step_ashore";
                return _activeBoostMode;
            }

            _activeBoostMode = "swim";
            return _activeBoostMode;
        }

        // Priority Gate C: Wall-Follow / Corner-Escape Safety (never jump or dash into walls)
        if (isDetouring || wallAhead)
        {
            _jumpTriggerTimer = 0f;
            _rapidJumpActiveTimer = 0f;
            _gapDoubleJumpTimer = 0f;
            _stuckCounter = 0;
            _hasProgressAnchor = false;
            _progressWindowTimer = 0f;
            _sidestepFlipTimer = 0f;
            _activeBoostMode = "detour_run";
            return _activeBoostMode;
        }

        // Priority Gate C.5: Controlled drop-vault over a verified low railing/rim onto lower safe ground
        if (dropVaultAhead)
        {
            _jumpTriggerTimer = 0f;
            _rapidJumpActiveTimer = 0f;
            _gapDoubleJumpTimer = 0f;
            _stuckCounter = 0;
            _hasProgressAnchor = false;
            _progressWindowTimer = 0f;

            if (_config.EnableObstacleJump
                && HasStaminaForBoost()
                && _rapidJumpPulseCooldown <= 0f
                && !_jumpHeld
                && InvokeJump(true))
            {
                _jumpHeld = true;
                _jumpReleaseTimer = 0.08f;
                _rapidJumpPulseCooldown = 0.55f;
            }

            _activeBoostMode = "jump_drop_vault";
            return _activeBoostMode;
        }

        // Priority Gate C.6: Stepping-stone / air-gap crossing (launch Jump + timed double-jump across verified landing)
        if (gapJumpAhead)
        {
            _jumpTriggerTimer = 0f;
            _rapidJumpActiveTimer = 0f;
            _stuckCounter = 0;
            _hasProgressAnchor = false;
            _progressWindowTimer = 0f;

            if (_gapDoubleJumpTimer > 0f)
            {
                _gapDoubleJumpTimer -= dt;
                if (_gapDoubleJumpTimer <= 0f
                    && _config.EnableObstacleJump
                    && !_jumpHeld
                    && InvokeJump(true))
                {
                    _jumpHeld = true;
                    _jumpReleaseTimer = 0.08f;
                }
            }
            else if (_config.EnableObstacleJump
                && _rapidJumpPulseCooldown <= 0f
                && !_jumpHeld)
            {
                if (gapJumpDist >= 5.0f && HasStaminaForBoost() && !_rushHeld && InvokeRush(true))
                {
                    _rushHeld = true;
                    _rushReleaseTimer = 0.06f;
                }

                if (InvokeJump(true))
                {
                    _jumpHeld = true;
                    _jumpReleaseTimer = 0.08f;
                    _rapidJumpPulseCooldown = 0.70f;
                    if (gapJumpDist >= 3.3f || gapJumpDy >= 0.70f)
                        _gapDoubleJumpTimer = 0.27f;
                }
            }

            _activeBoostMode = $"gap_jump({gapJumpDist:F1}m)";
            return _activeBoostMode;
        }

        // Priority Gate D: Cliff / Drop-Off Safety (never cast Phantom Dash or Bunnyhop off an edge)
        if (cliffAhead)
        {
            _jumpTriggerTimer = 0f;
            _rapidJumpActiveTimer = 0f;
            _stuckCounter = 0;
            _hasProgressAnchor = false;
            _progressWindowTimer = 0f;
            _activeBoostMode = "cliff_caution";
            return _activeBoostMode;
        }

        // Ensure character turns toward the movement vector for ~0.16s before firing directional dashes
        _driveWarmupTimer += dt;
        if (_driveWarmupTimer < 0.16f)
        {
            _hasProgressAnchor = true;
            _progressAnchorPos = localPos;
            _progressWindowTimer = 0f;
            _activeBoostMode = "aligning";
            return _activeBoostMode;
        }

        // Priority 0: Stuck-on-ledge detection & Vertical target elevation rapid multi-jump ('점프 연타')
        if (_config.EnableObstacleJump)
        {
            if (!_hasProgressAnchor)
            {
                _hasProgressAnchor = true;
                _progressAnchorPos = localPos;
                _progressWindowTimer = 0f;
            }
            else
            {
                _progressWindowTimer += dt;
                float checkWin = Math.Max(0.20f, _config.StuckCheckWindowSec);
                if (_progressWindowTimer >= checkWin)
                {
                    float mdx = localPos.x - _progressAnchorPos.x;
                    float mdz = localPos.z - _progressAnchorPos.z;
                    float movedHoriz = MathF.Sqrt(mdx * mdx + mdz * mdz);
                    float minProg = Math.Max(0.15f, _config.StuckMinProgressMeters);

                    _progressAnchorPos = localPos;
                    _progressWindowTimer = 0f;

                    if (_driveWarmupTimer >= 0.25f && movedHoriz < minProg)
                    {
                        _stuckCounter++;
                        _rapidJumpActiveTimer = Math.Max(_rapidJumpActiveTimer, 1.15f);
                    }
                    else if (movedHoriz >= minProg * 1.6f)
                    {
                        _stuckCounter = 0;
                    }
                }
            }

            if (stepAhead)
            {
                _rapidJumpActiveTimer = Math.Max(_rapidJumpActiveTimer, 0.35f);
            }

            float heightDiff = hasTargetElevation ? (targetPos.y - localPos.y) : 0f;
            float elevThresh = Math.Max(0.6f, _config.ElevationJumpThresholdMeters);
            bool targetIsHigher = hasTargetElevation && heightDiff >= elevThresh && heightDiff <= 2.8f && distanceToTarget <= 4.2f;
            if (targetIsHigher)
            {
                _rapidJumpActiveTimer = Math.Max(_rapidJumpActiveTimer, 0.55f);
            }

            if (_rapidJumpActiveTimer > 0f)
            {
                _rapidJumpActiveTimer -= dt;
                if (!_config.EnableSmartPathfinding && _stuckCounter >= 4)
                {
                    _sidestepFlipTimer += dt;
                    if (_sidestepFlipTimer >= 1.2f)
                    {
                        _sidestepFlipTimer = 0f;
                        _sidestepSign = -_sidestepSign;
                    }
                }
                else
                {
                    _sidestepFlipTimer = 0f;
                }

                // Cancel delayed single bunnyhop jump so rapid pulse drives Jump -> Double-Jump -> Parkour Climb cleanly
                _jumpTriggerTimer = 0f;

                if (_rapidJumpPulseCooldown <= 0f && !_jumpHeld)
                {
                    // Mix in a quick forward Rush burst on persistent ledges/cliffs when not facing a wall
                    if (!wallAhead && (_stuckCounter >= 2 || targetIsHigher) && _bunnyhopCooldownTimer <= 0f && !_rushHeld)
                    {
                        if (HasStaminaForBoost() && InvokeRush(true))
                        {
                            _rushHeld = true;
                            _rushReleaseTimer = 0.06f;
                            _bunnyhopCooldownTimer = 0.75f;
                        }
                    }

                    if (InvokeJump(true))
                    {
                        _jumpHeld = true;
                        _jumpReleaseTimer = 0.07f;
                        _rapidJumpPulseCooldown = Math.Max(0.16f, _config.RapidJumpIntervalSec);
                    }
                }

                _activeBoostMode = targetIsHigher
                    ? $"jump_climb(+{heightDiff:F1}m)"
                    : (stepAhead && _stuckCounter == 0 ? "jump_step_up" : $"jump_unstuck(#{_stuckCounter})");
                return _activeBoostMode;
            }
        }

        // Close range (flat ground, unstuck): cancel pending bunnyhop jump so we never overshoot the stop radius
        if (distanceToTarget < _config.BunnyhopMinDistance)
        {
            _jumpTriggerTimer = 0f;
            _activeBoostMode = "walk";
            return _activeBoostMode;
        }

        // Tier 1: Far distance -> Try 'E' / Phantom Dash (환영 대시) if off cooldown and not blocked by a wall
        if (_config.EnableMobilitySkill
            && !wallAhead
            && distanceToTarget >= _config.MobilitySkillMinDistance
            && _mobilityCheckTimer <= 0f
            && _jumpTriggerTimer <= 0f)
        {
            _mobilityCheckTimer = 0.85f;
            if (TryCastMobilitySkill(out int usedSlot, out string skillInfo))
            {
                // Enforce a 6s minimum recast window so Resonance attempts never starve Bunnyhop jumping!
                _mobilityCheckTimer = 6.0f;
                _postMobilityGraceTimer = 0.45f;
                _bunnyhopCooldownTimer = Math.Max(_bunnyhopCooldownTimer, 0.50f);
                _activeBoostMode = $"mobility_skill({skillInfo})";
                _services.Log.Info($"[AgyBridge][Boost] Cast mobility skill ({skillInfo}, slot={usedSlot}) at dist={distanceToTarget:F1}m");
                return _activeBoostMode;
            }
        }

        // Wait briefly while Phantom Dash animation propels character forward
        if (_postMobilityGraceTimer > 0f)
        {
            return _activeBoostMode;
        }

        // Tier 2: Mid/Far distance -> Dash (Rush) -> Jump -> Direction Stick Bunnyhop
        if (_config.EnableBunnyhop
            && distanceToTarget >= _config.BunnyhopMinDistance
            && _bunnyhopCooldownTimer <= 0f
            && _jumpTriggerTimer <= 0f)
        {
            if (HasStaminaForBoost() && InvokeRush(true))
            {
                _rushHeld = true;
                _rushReleaseTimer = 0.06f;
                _jumpTriggerTimer = Math.Max(0.06f, _config.BunnyhopDashToJumpSec);
                _bunnyhopCooldownTimer = Math.Max(0.55f, _config.BunnyhopCycleCooldownSec);
                _activeBoostMode = "bunnyhop";
                return _activeBoostMode;
            }
        }

        if (_jumpTriggerTimer <= 0f && _postMobilityGraceTimer <= 0f && _bunnyhopCooldownTimer <= 0.3f)
        {
            _activeBoostMode = "run";
        }
        return _activeBoostMode;
    }

    private void TickPendingReleases(float dt)
    {
        int frame = Time.frameCount;
        if (_lastReleaseTickFrame == frame) return;
        _lastReleaseTickFrame = frame;

        if (_rushReleaseTimer > 0f)
        {
            _rushReleaseTimer -= dt;
            if (_rushReleaseTimer <= 0f && _rushHeld)
            {
                InvokeRush(false);
                _rushHeld = false;
                _dodgePulseActive = false;
            }
        }

        if (_jumpTriggerTimer > 0f)
        {
            _jumpTriggerTimer -= dt;
            if (_jumpTriggerTimer <= 0f)
            {
                if (InvokeJump(true))
                {
                    _jumpHeld = true;
                    _jumpReleaseTimer = 0.07f;
                }
            }
        }

        if (_jumpReleaseTimer > 0f)
        {
            _jumpReleaseTimer -= dt;
            if (_jumpReleaseTimer <= 0f && _jumpHeld)
            {
                InvokeJump(false);
                _jumpHeld = false;
            }
        }

        if (_attackReleaseTimer > 0f)
        {
            _attackReleaseTimer -= dt;
            if (_attackReleaseTimer <= 0f && _activeAttackSlotRelease > 0)
            {
                InvokeAttack(_activeAttackSlotRelease, false);
                _activeAttackSlotRelease = 0;
            }
        }
    }

    /// <summary>
    /// Dynamically locates the configured mobility skill (e.g. '환영 대시' / 'E' Resonance or Skill slot),
    /// checks that it is off cooldown, and fires it.
    /// </summary>
    public bool TryCastMobilitySkill(out int castSlot, out string matchedSkill)
    {
        castSlot = 0;
        matchedSkill = "";
        if (!_movement.IsLuaReady) return false;

        var rObj = _movement.QueryReadyMobilitySkillSlot(
            _config.MobilitySkillSlot,
            _config.MobilitySkillNameHint ?? "환영");
        if (rObj == null)
            return false;

        bool ready = rObj["casted"]?.GetValue<bool>() == true;
        if (!ready) return false;

        castSlot = rObj["slotId"]?.GetValue<int>() ?? 0;
        matchedSkill = rObj["name"]?.GetValue<string>() ?? $"#{rObj["skillId"]}";

        if (castSlot <= 0 || !InvokeAttack(castSlot, true))
            return false;

        _activeAttackSlotRelease = castSlot;
        _attackReleaseTimer = 0.06f;
        return true;
    }

    /// <summary>
    /// Diagnostic helper to inspect all active skill slots and cooldown states in-game.
    /// </summary>
    public JsonObject InspectSkillSlots() => _movement.InspectSkillSlots();

    private void EnsureResolved()
    {
        if (_picResolved) return;
        _picResolved = true;
        try
        {
            var picType = InteropResolver.FindType("Panda.ZInput.PlayerInputController");
            if (picType != null)
            {
                _piPicInstance = picType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _miRush = picType.GetMethod("Rush", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                _miJump = picType.GetMethod("Jump", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                _miAttack = picType.GetMethod("Attack", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(bool) }, null);
                _miSwimSprint = picType.GetMethod("SwimSprint", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                _miGlideBool = picType.GetMethod("Glide", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                _miFlowBool = picType.GetMethod("Flow", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                _miResonance1Press = picType.GetMethod("handleUseResonanceSkill1", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _miResonance1Release = picType.GetMethod("handleUseResonanceSkill1Release", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _miResonance2Press = picType.GetMethod("handleUseResonanceSkill2", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _miResonance2Release = picType.GetMethod("handleUseResonanceSkill2Release", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                var argsType = InteropResolver.FindType("Panda.ZInput.InputActionEventArgs");
                _piEmptyInputArgs = argsType?.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static);
            }

            _services.Log.Info($"[AgyBridge][Boost] Resolved PlayerInputController actions (Rush={_miRush != null}, Jump={_miJump != null}, Attack={_miAttack != null}, SwimSprint={_miSwimSprint != null}, Glide={_miGlideBool != null}, Flow={_miFlowBool != null}, ResE={_miResonance2Press != null})");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Boost] Resolve failed: {ex.Message}");
        }
    }

    private void UpdateStaminaAndState(float dt)
    {
        if (_sprintBreakCooldown > 0f)
            _sprintBreakCooldown = MathF.Max(0f, _sprintBreakCooldown - dt);
        if (_sprintBreakPauseTimer > 0f)
            _sprintBreakPauseTimer = MathF.Max(0f, _sprintBreakPauseTimer - dt);

        _movement.ReadNativePlayerMetrics(out float curStam, out float maxStam, out int actorState, out int moveType);
        if (maxStam <= 0f)
        {
            curStam = _services.PlayerState.Stamina;
            maxStam = _services.PlayerState.MaxStamina;
        }

        _lastCurStamina = curStam;
        _lastMaxStamina = maxStam;
        _lastActorState = actorState;
        _lastVirtualMoveType = moveType;

        if (maxStam <= 0f)
        {
            _lastStaminaRatio = -1f;
            _conservingStamina = false;
            _prevRawStamina = -1f;
            return;
        }

        float conserveRatio = Math.Clamp(_config.LowStaminaConserveRatio, 0.10f, 0.90f);
        float resumeRatio = Math.Clamp(_config.LowStaminaResumeRatio, conserveRatio + 0.03f, 0.98f);

        _lastStaminaRatio = Math.Clamp(curStam / maxStam, 0f, 1f);
        bool wasConserving = _conservingStamina;
        if (_lastStaminaRatio <= conserveRatio)
            _conservingStamina = true;
        else if (_lastStaminaRatio >= resumeRatio)
            _conservingStamina = false;

        bool staminaDraining = _prevRawStamina > 0f && curStam < _prevRawStamina - 1.5f;
        bool inLatchedSprint = moveType == MoveParkourRun || moveType == MoveDash || actorState == ActorStateSwim;

        if (_conservingStamina)
        {
            if (actorState == ActorStateGlide)
                InvokeGlide(false);
            else if (actorState == ActorStateFlow)
                InvokeFlow(false);

            if (!wasConserving || (staminaDraining && inLatchedSprint && _sprintBreakCooldown <= 0f))
            {
                InvokeSwimSprint(false);
                if (inLatchedSprint)
                {
                    _sprintBreakPauseTimer = 0.10f;
                    _sprintBreakCooldown = 1.6f;
                }
            }
        }

        _prevRawStamina = curStam;
    }

    private bool HasStaminaForBoost()
    {
        float conserveRatio = Math.Clamp(_config.LowStaminaConserveRatio, 0.10f, 0.90f);
        return _lastMaxStamina > 0f
            && !_conservingStamina
            && _lastStaminaRatio > conserveRatio
            && _lastCurStamina >= _config.BunnyhopMinStamina;
    }

    private void CancelBoostForStaminaConserve()
    {
        if (_jumpHeld)
        {
            InvokeJump(false);
            _jumpHeld = false;
        }
        _jumpReleaseTimer = 0f;

        if (_rushHeld && !_dodgePulseActive)
        {
            InvokeRush(false);
            _rushHeld = false;
            _rushReleaseTimer = 0f;
        }

        InvokeSwimSprint(false);
        _jumpTriggerTimer = 0f;
        _rapidJumpActiveTimer = 0f;
        _gapDoubleJumpTimer = 0f;
        _stuckCounter = 0;
        _hasProgressAnchor = false;
        _progressWindowTimer = 0f;
        _sidestepFlipTimer = 0f;
    }

    private bool InvokeSwimSprint(bool pressed)
    {
        try
        {
            var pic = _piPicInstance?.GetValue(null);
            if (pic == null || _miSwimSprint == null) return false;
            _miSwimSprint.Invoke(pic, new object[] { pressed });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool InvokeGlide(bool isEnter)
    {
        try
        {
            var pic = _piPicInstance?.GetValue(null);
            if (pic == null || _miGlideBool == null) return false;
            _miGlideBool.Invoke(pic, new object[] { isEnter });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool InvokeFlow(bool isEnter)
    {
        try
        {
            var pic = _piPicInstance?.GetValue(null);
            if (pic == null || _miFlowBool == null) return false;
            _miFlowBool.Invoke(pic, new object[] { isEnter });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool InvokeRush(bool pressed)
    {
        try
        {
            var pic = _piPicInstance?.GetValue(null);
            if (pic == null || _miRush == null) return false;
            _miRush.Invoke(pic, new object[] { pressed });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool InvokeJump(bool pressed)
    {
        try
        {
            var pic = _piPicInstance?.GetValue(null);
            if (pic == null || _miJump == null) return false;
            _miJump.Invoke(pic, new object[] { pressed });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool InvokeAttack(int slotId, bool pressed)
    {
        try
        {
            var pic = _piPicInstance?.GetValue(null);
            if (pic == null) return false;

            if (slotId == 101)
            {
                var mi = pressed ? _miResonance1Press : _miResonance1Release;
                if (mi != null)
                {
                    object? emptyArgs = null;
                    try { emptyArgs = _piEmptyInputArgs?.GetValue(null); } catch { }
                    mi.Invoke(pic, new object?[] { emptyArgs });
                    return true;
                }
            }
            else if (slotId == 102)
            {
                var mi = pressed ? _miResonance2Press : _miResonance2Release;
                if (mi != null)
                {
                    object? emptyArgs = null;
                    try { emptyArgs = _piEmptyInputArgs?.GetValue(null); } catch { }
                    mi.Invoke(pic, new object?[] { emptyArgs });
                    return true;
                }
            }

            if (_miAttack == null) return false;
            _miAttack.Invoke(pic, new object[] { slotId, pressed });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
