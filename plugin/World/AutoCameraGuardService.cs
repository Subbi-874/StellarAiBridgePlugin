using System;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Core;
using UnityEngine;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Automatically restores maximum zoom-out and an overhead top-down camera angle
/// after ultimate skill cutscenes, map/portal warps, loading screens, or when Auto-Combat is active,
/// while yielding cleanly whenever the human player moves or scrolls the mouse.
/// </summary>
public sealed class AutoCameraGuardService : IDisposable
{
    private const int CameraStateDefault = 0;

    private static volatile bool _bigSkillEndedFlag;

    private static void PostfixEndBigSkillCamera()
    {
        _bigSkillEndedFlag = true;
    }

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly WorldStateService _world;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly BehaviorRunner _behaviors;

    private Harmony? _harmony;
    private bool _resolved;
    private bool _ready;

    private PropertyInfo? _piCamMgrInst;
    private PropertyInfo? _piCamCurrentState;
    private PropertyInfo? _piMainCamTrans;
    private PropertyInfo? _piStateMachineContext;
    private MemberInfo? _miOrFiCamAnim;
    private MethodInfo? _miGetContextMember;
    private MethodInfo? _miSetContextMember;
    private MethodInfo? _miGetCmFreeLook;
    private MethodInfo? _miSetCmFreeLook;

    private object? _enumZoomMax;
    private object? _enumCurZoom;
    private object? _enumCmVertical;

    private PropertyInfo? _piPicInst;
    private MethodInfo? _miPicZoom;
    private MethodInfo? _miPicRotation;

    private PropertyInfo? _piEntMgrInst;
    private PropertyInfo? _piPlayerEnt;
    private PropertyInfo? _piPlayerEntity;
    private MethodInfo? _miGetLuaAutoBattle;

    // Lifecycle & transition tracking
    private bool _wasLoading;
    private int _lastSceneId;
    private bool _hasLastPlayerPos;
    private Vector3 _lastPlayerPos;
    private int _lastCamState = -1;
    private bool _wasCamAnim;
    private bool _wasAutoCombatOn;

    // Restore scheduling & closed-loop pulse guard
    private float _pendingDelay;
    private float _restoreWindow;
    private float _recentTransitionGrace;
    private float _manualYieldTimer;
    private float _periodicCheckTimer = 1.5f;
    private float _directApplyCooldown;
    private float _pulseCooldown;

    private float _zoomOutSign = -1f;
    private float _pitchOverheadSign = 1f;
    private bool _hasPrevPulseZoom;
    private float _prevPulseZoom;
    private bool _hasPrevPulseElev;
    private float _prevPulseElevDeg;

    public AutoCameraGuardService(
        IPluginServices services,
        BridgeConfig config,
        WorldStateService world,
        AutoCombatKeeper autoCombat,
        BehaviorRunner behaviors)
    {
        _services = services;
        _config = config;
        _world = world;
        _autoCombat = autoCombat;
        _behaviors = behaviors;
    }

    public void Tick(float dt)
    {
        if (!_config.EnableAutoCameraGuard)
        {
            _pendingDelay = 0f;
            _restoreWindow = 0f;
            _recentTransitionGrace = 0f;
            return;
        }

        EnsureResolved();

        if (_manualYieldTimer > 0f)
            _manualYieldTimer = MathF.Max(0f, _manualYieldTimer - dt);
        if (_recentTransitionGrace > 0f)
            _recentTransitionGrace = MathF.Max(0f, _recentTransitionGrace - dt);
        if (_directApplyCooldown > 0f)
            _directApplyCooldown = MathF.Max(0f, _directApplyCooldown - dt);
        if (_pulseCooldown > 0f)
            _pulseCooldown = MathF.Max(0f, _pulseCooldown - dt);

        if (DetectManualCameraInput())
        {
            _manualYieldTimer = 3.5f;
            _pendingDelay = 0f;
            _restoreWindow = 0f;
            ResetPulseTracking();
        }

        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _wasLoading = true;
            _pendingDelay = 0f;
            _restoreWindow = 0f;
            ResetPulseTracking();
            return;
        }

        int sceneId = _world.GetLocalSceneId();
        var sp = _services.PlayerState.Position;
        var playerVec = new Vector3(sp.X, sp.Y, sp.Z);

        bool sceneChanged = sceneId > 0 && _lastSceneId > 0 && sceneId != _lastSceneId;
        bool bigWarpJump = _hasLastPlayerPos && HorizontalDistSq(playerVec, _lastPlayerPos) >= 25.0f * 25.0f;
        if (sceneId > 0)
            _lastSceneId = sceneId;
        _lastPlayerPos = playerVec;
        _hasLastPlayerPos = true;

        ReadCameraRuntime(out object? camMgr, out int camState, out bool isCamAnim, out float curZoom, out float zoomMax, out float curVert, out float elevDeg);

        if (_wasLoading || sceneChanged || bigWarpJump)
        {
            _wasLoading = false;
            QueueRestore(delay: 0.35f, window: 0.65f, grace: 5.0f);
        }

        bool bigSkillEnded = _bigSkillEndedFlag;
        _bigSkillEndedFlag = false;
        bool stateReturnedToDefault = _lastCamState > 0 && camState == CameraStateDefault;
        bool animFinished = _wasCamAnim && !isCamAnim;

        if (bigSkillEnded || (camState == CameraStateDefault && (stateReturnedToDefault || animFinished)))
        {
            QueueRestore(delay: 0.18f, window: 0.65f, grace: 4.0f);
        }

        bool autoCombatOn = _autoCombat.DesiredEnabled || ReadInGameAutoBattle();
        bool botActive = autoCombatOn || _behaviors.Current != null || _recentTransitionGrace > 0f;

        if (autoCombatOn && !_wasAutoCombatOn)
        {
            QueueRestore(delay: 0.10f, window: 0.60f, grace: 2.0f);
        }

        _wasAutoCombatOn = autoCombatOn;
        _lastCamState = camState;
        _wasCamAnim = isCamAnim;

        if (!_ready || camMgr == null)
            return;

        if (autoCombatOn && _manualYieldTimer <= 0f && camState == CameraStateDefault && !isCamAnim && _restoreWindow <= 0f)
        {
            _periodicCheckTimer -= dt;
            if (_periodicCheckTimer <= 0f)
            {
                _periodicCheckTimer = 2.5f;
                float targetRatio = Math.Clamp(_config.CameraTargetZoomRatio, 0.50f, 1.0f);
                bool zoomTooClose = zoomMax > 1.0f && curZoom < zoomMax * (targetRatio - 0.12f);
                bool pitchTooFlat = elevDeg > -90f && elevDeg < 22f;
                if (zoomTooClose || pitchTooFlat)
                {
                    QueueRestore(delay: 0f, window: 0.45f, grace: 0f);
                }
            }
        }

        if (!botActive || _manualYieldTimer > 0f || camState != CameraStateDefault || isCamAnim || _restoreWindow <= 0f)
            return;

        if (_pendingDelay > 0f)
        {
            _pendingDelay = MathF.Max(0f, _pendingDelay - dt);
            return;
        }

        _restoreWindow = MathF.Max(0f, _restoreWindow - dt);
        ExecuteCameraRestoreStep(camMgr, curZoom, zoomMax, curVert, elevDeg);
    }

    public JsonObject ForceRestoreNow(string reason = "rpc")
    {
        EnsureResolved();
        _manualYieldTimer = 0f;
        _pendingDelay = 0.02f;
        _restoreWindow = 0.65f;
        _recentTransitionGrace = 4.0f;
        ResetPulseTracking();

        ReadCameraRuntime(out _, out int camState, out bool isCamAnim, out float curZoom, out float zoomMax, out float curVert, out float elevDeg);
        return new JsonObject
        {
            ["ok"] = true,
            ["reason"] = reason,
            ["enabled"] = _config.EnableAutoCameraGuard,
            ["target_zoom_ratio"] = _config.CameraTargetZoomRatio,
            ["target_vertical"] = _config.CameraTargetVertical,
            ["cam_state"] = camState,
            ["is_cam_anim"] = isCamAnim,
            ["cur_zoom"] = MathF.Round(curZoom, 2),
            ["zoom_max"] = MathF.Round(zoomMax, 2),
            ["cur_vertical"] = MathF.Round(curVert, 3),
            ["elev_deg"] = elevDeg > -90f ? MathF.Round(elevDeg, 1) : null
        };
    }

    public void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _bigSkillEndedFlag = false;
    }

    private void QueueRestore(float delay, float window, float grace)
    {
        _pendingDelay = _restoreWindow > 0f ? MathF.Min(_pendingDelay, delay) : delay;
        _restoreWindow = MathF.Max(_restoreWindow, window);
        _recentTransitionGrace = MathF.Max(_recentTransitionGrace, grace);
        ResetPulseTracking();
    }

    private void ResetPulseTracking()
    {
        _hasPrevPulseZoom = false;
        _hasPrevPulseElev = false;
    }

    private void ExecuteCameraRestoreStep(object camMgr, float curZoom, float zoomMax, float curVert, float elevDeg)
    {
        float zoomRatio = Math.Clamp(_config.CameraTargetZoomRatio, 0.50f, 1.0f);
        float targetVert = Math.Clamp(_config.CameraTargetVertical, 0.45f, 0.96f);
        float targetZoom = zoomMax > 0.5f ? zoomMax * zoomRatio : 0f;
        const float targetElevDeg = 32f;

        bool zoomSatisfied = zoomMax <= 0.5f || curZoom >= targetZoom - 0.15f;
        bool pitchSatisfied = (elevDeg > -90f && elevDeg >= targetElevDeg) || curVert >= targetVert - 0.04f;

        if (zoomSatisfied && pitchSatisfied)
        {
            _restoreWindow = 0f;
            ResetPulseTracking();
            return;
        }

        if (_directApplyCooldown <= 0f)
        {
            _directApplyCooldown = 0.16f;
            if (!zoomSatisfied && _miSetContextMember != null && _enumCurZoom != null)
            {
                try { _miSetContextMember.Invoke(camMgr, new[] { _enumCurZoom, targetZoom }); } catch { }
            }
            if (!pitchSatisfied && _miSetCmFreeLook != null && _enumCmVertical != null && curVert < targetVert - 0.03f)
            {
                try { _miSetCmFreeLook.Invoke(camMgr, new[] { _enumCmVertical, targetVert }); } catch { }
            }
        }

        if (_pulseCooldown > 0f)
            return;
        _pulseCooldown = 0.07f;

        if (!zoomSatisfied && zoomMax > 0.5f && curZoom < targetZoom - 0.15f)
        {
            if (_hasPrevPulseZoom && curZoom < _prevPulseZoom - 0.02f)
                _zoomOutSign = -_zoomOutSign;
            _prevPulseZoom = curZoom;
            _hasPrevPulseZoom = true;
            InvokeZoom(_zoomOutSign * 2.5f);
        }

        if (!pitchSatisfied && curVert < targetVert - 0.04f)
        {
            if (elevDeg > -90f)
            {
                if (_hasPrevPulseElev && elevDeg < _prevPulseElevDeg - 0.4f)
                    _pitchOverheadSign = -_pitchOverheadSign;
                _prevPulseElevDeg = elevDeg;
                _hasPrevPulseElev = true;
            }
            InvokeRotation(new Vector2(0f, _pitchOverheadSign * 3.2f));
        }
    }

    private void EnsureResolved()
    {
        if (_resolved)
            return;
        _resolved = true;

        try
        {
            var cmType = InteropResolver.FindType("Panda.ZGame.CameraManager");
            var picType = InteropResolver.FindType("Panda.ZInput.PlayerInputController");
            var emType = InteropResolver.FindType("Panda.ZGame.ZEntityMgr");
            var ctxEnum = InteropResolver.FindType("Panda.ZGame.EContextMember");
            var flEnum = InteropResolver.FindType("Panda.ZGame.ECmFreeLookType");

            if (cmType != null)
            {
                _piCamMgrInst = cmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piCamCurrentState = cmType.GetProperty("CurrentState", BindingFlags.Public | BindingFlags.Instance);
                _piMainCamTrans = cmType.GetProperty("MainCamTrans", BindingFlags.Public | BindingFlags.Instance);
                _piStateMachineContext = cmType.GetProperty("StateMachineContext", BindingFlags.Public | BindingFlags.Instance);

                if (ctxEnum != null)
                {
                    _enumZoomMax = Enum.ToObject(ctxEnum, 1);
                    _enumCurZoom = Enum.ToObject(ctxEnum, 3);
                    _miGetContextMember = cmType.GetMethod("GetContextMember", BindingFlags.Public | BindingFlags.Instance, null, new[] { ctxEnum }, null);
                    _miSetContextMember = cmType.GetMethod("SetContextMember", BindingFlags.Public | BindingFlags.Instance, null, new[] { ctxEnum, typeof(float) }, null);
                }

                if (flEnum != null)
                {
                    _enumCmVertical = Enum.ToObject(flEnum, 3);
                    _miGetCmFreeLook = cmType.GetMethod("GetCmFreeLook", BindingFlags.Public | BindingFlags.Instance, null, new[] { flEnum }, null);
                    _miSetCmFreeLook = cmType.GetMethod("SetCmFreeLook", BindingFlags.Public | BindingFlags.Instance, null, new[] { flEnum, typeof(float) }, null);
                }

                var miEndBig = cmType.GetMethod("EndBigSkillCamera", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (miEndBig != null)
                {
                    try
                    {
                        _harmony = _services.Harmony.Create("agybridge.cameraguard");
                        _harmony.Patch(miEndBig, postfix: new HarmonyMethod(typeof(AutoCameraGuardService), nameof(PostfixEndBigSkillCamera)));
                    }
                    catch (Exception hex)
                    {
                        _services.Log.Warning($"[AgyBridge][Cam] Harmony patch EndBigSkillCamera skipped: {hex.Message}");
                    }
                }
            }

            if (picType != null)
            {
                _piPicInst = picType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _miPicZoom = picType.GetMethod("Zoom", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
                _miPicRotation = picType.GetMethod("Rotation", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector2) }, null);
            }

            if (emType != null)
            {
                _piEntMgrInst = emType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _piPlayerEnt = emType.GetProperty("PlayerEnt", BindingFlags.Public | BindingFlags.Instance);
                _piPlayerEntity = emType.GetProperty("PlayerEntity", BindingFlags.Public | BindingFlags.Instance);
            }

            _ready = _piCamMgrInst != null && (_miSetContextMember != null || _miSetCmFreeLook != null || _miPicZoom != null);
            _services.Log.Info($"[AgyBridge][Cam] Initialized AutoCameraGuard (ready={_ready}, EndBigSkillHook={_harmony != null}, CtxZoom={_miSetContextMember != null}, CmVert={_miSetCmFreeLook != null}, PicZoom={_miPicZoom != null})");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Cam] Resolve error: {ex.Message}");
        }
    }

    private void ReadCameraRuntime(
        out object? camMgr,
        out int camState,
        out bool isCamAnim,
        out float curZoom,
        out float zoomMax,
        out float curVert,
        out float elevDeg)
    {
        camMgr = null;
        camState = CameraStateDefault;
        isCamAnim = false;
        curZoom = 0f;
        zoomMax = 0f;
        curVert = 0f;
        elevDeg = -999f;

        if (!_ready) return;

        try
        {
            camMgr = _piCamMgrInst?.GetValue(null);
            if (camMgr == null) return;

            if (_piCamCurrentState?.GetValue(camMgr) is object stObj)
                camState = Convert.ToInt32(stObj, CultureInfo.InvariantCulture);

            object? fsmCtx = _piStateMachineContext?.GetValue(camMgr);
            if (fsmCtx != null)
            {
                if (_miOrFiCamAnim == null)
                {
                    var ct = fsmCtx.GetType();
                    _miOrFiCamAnim = (MemberInfo?)ct.GetProperty("isCameraAnimation_", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? ct.GetField("isCameraAnimation_", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                object? animVal = _miOrFiCamAnim switch
                {
                    PropertyInfo pi => pi.GetValue(fsmCtx),
                    FieldInfo fi => fi.GetValue(fsmCtx),
                    _ => null
                };
                if (animVal is bool b)
                    isCamAnim = b;
            }

            if (_miGetContextMember != null && _enumCurZoom != null)
            {
                object? z = _miGetContextMember.Invoke(camMgr, new[] { _enumCurZoom });
                if (z != null) curZoom = Convert.ToSingle(z, CultureInfo.InvariantCulture);
            }
            if (_miGetContextMember != null && _enumZoomMax != null)
            {
                object? zm = _miGetContextMember.Invoke(camMgr, new[] { _enumZoomMax });
                if (zm != null) zoomMax = Convert.ToSingle(zm, CultureInfo.InvariantCulture);
            }
            if (_miGetCmFreeLook != null && _enumCmVertical != null)
            {
                object? v = _miGetCmFreeLook.Invoke(camMgr, new[] { _enumCmVertical });
                if (v != null) curVert = Convert.ToSingle(v, CultureInfo.InvariantCulture);
            }

            if (_piMainCamTrans?.GetValue(camMgr) is Transform camTr && camTr != null)
            {
                var cPos = camTr.position;
                var sp = _services.PlayerState.Position;
                float dx = cPos.x - sp.X;
                float dz = cPos.z - sp.Z;
                float dy = cPos.y - (sp.Y + 1.2f);
                float horiz = MathF.Sqrt(dx * dx + dz * dz);
                if (horiz > 0.25f || MathF.Abs(dy) > 0.25f)
                    elevDeg = MathF.Atan2(dy, MathF.Max(0.01f, horiz)) * (180f / MathF.PI);
            }
        }
        catch { }
    }

    private bool ReadInGameAutoBattle()
    {
        try
        {
            var mgr = _piEntMgrInst?.GetValue(null);
            object? ent = mgr == null ? null : (_piPlayerEnt?.GetValue(mgr) ?? _piPlayerEntity?.GetValue(mgr));
            if (ent == null) return false;

            _miGetLuaAutoBattle ??= ent.GetType().GetMethod(
                "GetLuaAutoBattle",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            return _miGetLuaAutoBattle?.Invoke(ent, null) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private void InvokeZoom(float delta)
    {
        try
        {
            var pic = _piPicInst?.GetValue(null);
            if (pic != null && _miPicZoom != null)
                _miPicZoom.Invoke(pic, new object[] { delta });
        }
        catch { }
    }

    private void InvokeRotation(Vector2 delta)
    {
        try
        {
            var pic = _piPicInst?.GetValue(null);
            if (pic != null && _miPicRotation != null)
                _miPicRotation.Invoke(pic, new object[] { delta });
        }
        catch { }
    }

    private static bool DetectManualCameraInput()
    {
        try
        {
            if (!Application.isFocused)
                return false;
            if (MathF.Abs(Input.GetAxisRaw("Mouse ScrollWheel")) > 0.01f)
                return true;
            if ((Input.GetMouseButton(0) || Input.GetMouseButton(1))
                && (MathF.Abs(Input.GetAxisRaw("Mouse X")) + MathF.Abs(Input.GetAxisRaw("Mouse Y"))) > 0.35f)
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    private static float HorizontalDistSq(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}
