using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stellar.AgyBridge.Core;

/// <summary>
/// Manages 3-stage resource-saving modes ("off" | "standard" | "ultra") inside the running game client:
/// throttles idle FPS and OnDemandRendering intervals, turns off shadows and 3D world cameras in ultra mode
/// (preserving UICamera and Stellar overlays), scales down texture mipmaps, and trims idle memory.
/// </summary>
public sealed class EcoModeService : IDisposable
{
    public const string ModeOff = "off";
    public const string ModeStandard = "standard";
    public const string ModeUltra = "ultra";

    private sealed class SavedCameraState
    {
        public Camera Camera { get; init; } = null!;
        public int OriginalCullingMask { get; init; }
        public CameraClearFlags OriginalClearFlags { get; init; }
        public Color OriginalBackgroundColor { get; init; }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;

    private string _activeMode = ModeOff;
    private bool _effectiveApplied;
    private bool _pausedByFocus;
    private bool _relaxedForActivity;
    private float _unfocusedCooldown;
    private float _activityHoldTimer;
    private bool _lastActivitySignal;
    private bool _lastAppliedFocused;
    private bool _lastAppliedActivity;
    private float _reassertTimer;
    private float _trimTimer = 20f;
    private bool _trimmedForCurrentUltraEntry;
    private bool _disposed;

    // Capability latches prevent repeated Method unstripping warnings on IL2CPP builds
    private bool _vSyncSupported = true;
    private bool _targetFpsSupported = true;
    private bool _onDemandSupported = true;
    private bool _shadowDistanceSupported = true;

    // Baseline engine state captured once before any eco override
    private bool _hasBaseline;
    private int _origTargetFrameRate = -1;
    private int _origVSyncCount = 1;
    private int _origRenderFrameInterval = 1;
    private float _origShadowDistance = 120f;
    private int _origMipmapLimit;
    private float _origAudioVolume = 1f;
    private ProcessPriorityClass _origPriority = ProcessPriorityClass.Normal;
    private bool _priorityCaptured;

    private readonly Dictionary<int, SavedCameraState> _blankedCameras = new();

    private PropertyInfo? _piTextureLimit;
    private bool _textureLimitProbed;
    private PropertyInfo? _piAudioVolume;
    private bool _audioProbed;

    public string ConfiguredMode => _config.EcoMode;
    public bool IsPausedByFocus => _pausedByFocus;
    public bool IsRelaxedForActivity => _relaxedForActivity;

    public string EffectiveMode
    {
        get
        {
            if (_config.EcoMode == ModeOff || _pausedByFocus) return ModeOff;
            if (_config.EcoMode == ModeStandard)
                return _relaxedForActivity ? ModeStandard : ModeUltra;
            return ModeUltra;
        }
    }

    public EcoModeService(IPluginServices services, BridgeConfig config)
    {
        _services = services;
        _config = config;
        _activeMode = _config.EcoMode;
    }

    public static string NormalizeMode(string? raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "1" or "std" or "standard" or "smart" or "표준" or "일반절전" or "스마트" or "スマート" or "一般省電力" or "省電力" => ModeStandard,
            "2" or "max" or "extreme" or "black" or "ultra" or "극한" or "최대" or "최대절전" or "最大省電力" => ModeUltra,
            "0" or "off" or "none" or "false" or "normal" or "끔" or "해제" or "일반모드" or "通常" or "通常モード" or "オフ" => ModeOff,
            _ => ""
        };
    }

    public JsonObject SetMode(string mode, bool? pauseOnFocus = null, bool persist = true)
    {
        string norm = NormalizeMode(mode);
        if (string.IsNullOrEmpty(norm))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"지원하지 않는 절전 모드입니다: '{mode}' (off | standard | ultra)"
            };
        }

        _config.EcoMode = norm;
        if (pauseOnFocus.HasValue)
            _config.EcoAutoPauseOnFocus = pauseOnFocus.Value;
        if (persist)
            _config.Save();

        bool inWorld = _services.ClientState.Phase == GamePhase.World;
        bool focused = IsWindowFocused();

        if (norm == ModeOff || !inWorld)
        {
            _pausedByFocus = false;
            _relaxedForActivity = false;
            _trimmedForCurrentUltraEntry = false;
            _unfocusedCooldown = 0f;
            _reassertTimer = 0f;
            if (_effectiveApplied || _blankedCameras.Count > 0 || _hasBaseline)
                RestoreAll();
        }
        else if (_config.EcoAutoPauseOnFocus && focused)
        {
            _pausedByFocus = true;
            _unfocusedCooldown = 3.0f;
            _relaxedForActivity = false;
            _trimmedForCurrentUltraEntry = false;
            if (_effectiveApplied || _blankedCameras.Count > 0 || _hasBaseline)
                RestoreAll();
        }
        else
        {
            _pausedByFocus = false;
            _unfocusedCooldown = 0f;
            _reassertTimer = 0f;
            bool effectiveActivity = _lastActivitySignal || _activityHoldTimer > 0f;
            _relaxedForActivity = norm == ModeStandard && effectiveActivity;
            ApplyImmediately(effectiveActivity, focused, inWorld);

            if (EffectiveMode == ModeUltra && !focused && (_services.ClientState.UiState & GameUIState.Loading) == 0)
            {
                _trimmedForCurrentUltraEntry = true;
                _trimTimer = effectiveActivity ? 120.0f : 180.0f;
                TrimMemoryNow(emptyWorkingSet: true, unloadAssets: !effectiveActivity);
            }
            else
            {
                _trimmedForCurrentUltraEntry = false;
            }
        }

        return GetStatusJson().WithOk();
    }

    public string CycleMode()
    {
        string next = _config.EcoMode switch
        {
            ModeOff => ModeStandard,
            ModeStandard => ModeUltra,
            _ => ModeOff
        };
        SetMode(next);
        return next;
    }

    public void Tick(float deltaTime, bool activityActive, bool inWorld)
    {
        if (_disposed) return;

        _lastActivitySignal = activityActive;
        if (activityActive)
            _activityHoldTimer = 3.0f;
        else if (_activityHoldTimer > 0f)
            _activityHoldTimer = MathF.Max(0f, _activityHoldTimer - deltaTime);

        bool effectiveActivity = activityActive || _activityHoldTimer > 0f;

        string targetMode = _config.EcoMode;
        if (targetMode == ModeOff || !inWorld)
        {
            if (_effectiveApplied)
                RestoreAll();
            _pausedByFocus = false;
            _relaxedForActivity = false;
            _trimmedForCurrentUltraEntry = false;
            return;
        }

        bool focused = IsWindowFocused();
        if (_config.EcoAutoPauseOnFocus)
        {
            if (focused)
            {
                _unfocusedCooldown = 3.0f;
                _relaxedForActivity = false;
                _trimmedForCurrentUltraEntry = false;
                if (!_pausedByFocus)
                {
                    _pausedByFocus = true;
                    RestoreAll();
                }
                return;
            }
            if (_unfocusedCooldown > 0f)
            {
                _unfocusedCooldown = MathF.Max(0f, _unfocusedCooldown - deltaTime);
                return;
            }
            _pausedByFocus = false;
        }
        else
        {
            _pausedByFocus = false;
        }

        _relaxedForActivity = targetMode == ModeStandard && effectiveActivity;
        string desiredEffective = EffectiveMode;
        bool stateChanged = !_effectiveApplied
            || _activeMode != desiredEffective
            || _lastAppliedFocused != focused
            || _lastAppliedActivity != effectiveActivity;

        _reassertTimer -= deltaTime;
        if (stateChanged || _reassertTimer <= 0f)
        {
            _reassertTimer = 1.5f;
            ApplyImmediately(effectiveActivity, focused, inWorld);
        }

        bool isLoading = (_services.ClientState.UiState & GameUIState.Loading) != 0;
        if (desiredEffective == ModeUltra && !focused && !isLoading)
        {
            if (!_trimmedForCurrentUltraEntry)
            {
                _trimmedForCurrentUltraEntry = true;
                _trimTimer = effectiveActivity ? 120.0f : 180.0f;
                TrimMemoryNow(emptyWorkingSet: true, unloadAssets: !effectiveActivity);
            }
            else
            {
                _trimTimer -= deltaTime;
                if (_trimTimer <= 0f)
                {
                    _trimTimer = effectiveActivity ? 120.0f : 180.0f;
                    TrimMemoryNow(emptyWorkingSet: true, unloadAssets: !effectiveActivity);
                }
            }
        }
        else if (desiredEffective != ModeUltra)
        {
            _trimmedForCurrentUltraEntry = false;
        }
    }

    private static bool IsWindowFocused()
    {
        try { return Application.isFocused; }
        catch { return false; }
    }

    private void TryRunRenderOp(ref bool supported, string opName, Action action)
    {
        if (!supported) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            supported = false;
            _services.Log.Warning($"[AgyBridge][Eco] {opName} unsupported ({ex.Message}); skipping future calls.");
        }
    }

    private void ApplyImmediately(bool activityActive, bool focused, bool inWorld)
    {
        string mode = EffectiveMode;
        if (mode == ModeOff || !inWorld)
        {
            if (_effectiveApplied)
                RestoreAll();
            return;
        }

        CaptureBaselineIfNeeded();
        _activeMode = mode;
        _effectiveApplied = true;
        _lastAppliedFocused = focused;
        _lastAppliedActivity = activityActive;

        int targetFps = mode == ModeUltra
            ? (activityActive ? 25 : 10)
            : (activityActive ? 30 : 15);
        if (focused)
            targetFps = Math.Max(targetFps, 20);

        // Never skip frames while window has focus or combat/behavior activity is running
        int renderInterval = (focused || activityActive)
            ? 1
            : (mode == ModeUltra ? 8 : 4);

        TryRunRenderOp(ref _vSyncSupported, "QualitySettings.vSyncCount", () => QualitySettings.vSyncCount = 0);
        TryRunRenderOp(ref _targetFpsSupported, "Application.targetFrameRate", () => Application.targetFrameRate = targetFps);
        TryRunRenderOp(ref _onDemandSupported, "OnDemandRendering.renderFrameInterval", () => OnDemandRendering.renderFrameInterval = renderInterval);
        TryRunRenderOp(ref _shadowDistanceSupported, "QualitySettings.shadowDistance", () => QualitySettings.shadowDistance = 0f);

        if (mode == ModeUltra)
            SetTextureMipmapLimit(3);
        else
            SetTextureMipmapLimit(_origMipmapLimit);

        SetAudioMuted(true);
        SetProcessEcoPriority(true);

        if (mode == ModeUltra)
            BlankWorldCameras();
        else
            RestoreBlankedCameras();
    }

    private void RestoreAll()
    {
        RestoreBlankedCameras();
        SetProcessEcoPriority(false);
        SetAudioMuted(false);

        if (_hasBaseline)
        {
            TryRunRenderOp(ref _targetFpsSupported, "Application.targetFrameRate(restore)", () => Application.targetFrameRate = _origTargetFrameRate);
            TryRunRenderOp(ref _vSyncSupported, "QualitySettings.vSyncCount(restore)", () => QualitySettings.vSyncCount = _origVSyncCount);
            TryRunRenderOp(ref _onDemandSupported, "OnDemandRendering.renderFrameInterval(restore)", () => OnDemandRendering.renderFrameInterval = Math.Max(1, _origRenderFrameInterval));
            TryRunRenderOp(ref _shadowDistanceSupported, "QualitySettings.shadowDistance(restore)", () => QualitySettings.shadowDistance = _origShadowDistance);
            SetTextureMipmapLimit(_origMipmapLimit);
            _hasBaseline = false;
        }

        _effectiveApplied = false;
        _activeMode = ModeOff;
        _lastAppliedFocused = false;
        _lastAppliedActivity = false;
        _trimmedForCurrentUltraEntry = false;
    }

    private void CaptureBaselineIfNeeded()
    {
        if (_hasBaseline) return;
        TryRunRenderOp(ref _targetFpsSupported, "Application.targetFrameRate(capture)", () => _origTargetFrameRate = Application.targetFrameRate);
        TryRunRenderOp(ref _vSyncSupported, "QualitySettings.vSyncCount(capture)", () => _origVSyncCount = QualitySettings.vSyncCount);
        TryRunRenderOp(ref _onDemandSupported, "OnDemandRendering.renderFrameInterval(capture)", () => _origRenderFrameInterval = OnDemandRendering.renderFrameInterval);
        TryRunRenderOp(ref _shadowDistanceSupported, "QualitySettings.shadowDistance(capture)", () => _origShadowDistance = QualitySettings.shadowDistance);
        _origMipmapLimit = GetTextureMipmapLimit();
        _origAudioVolume = GetAudioVolume();
        _hasBaseline = true;
    }

    private void BlankWorldCameras()
    {
        try
        {
            var cams = Camera.allCameras;
            if (cams == null) return;

            var aliveIds = new HashSet<int>();
            for (int i = 0; i < cams.Length; i++)
            {
                var cam = cams[i];
                if (cam == null) continue;
                int id = cam.GetInstanceID();
                aliveIds.Add(id);

                // Keep dedicated UI camera (layer 5 mask == 32) visible so chat/Stellar windows stay interactive
                string nm = cam.name ?? "";
                if (nm.Contains("UI", StringComparison.OrdinalIgnoreCase) && cam.cullingMask == 32)
                    continue;

                if (!_blankedCameras.ContainsKey(id))
                {
                    _blankedCameras[id] = new SavedCameraState
                    {
                        Camera = cam,
                        OriginalCullingMask = cam.cullingMask,
                        OriginalClearFlags = cam.clearFlags,
                        OriginalBackgroundColor = cam.backgroundColor
                    };
                }

                if (cam.cullingMask != 0) cam.cullingMask = 0;
                if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
            }

            if (_blankedCameras.Count > aliveIds.Count)
            {
                var dead = new List<int>();
                foreach (var kv in _blankedCameras)
                {
                    if (!aliveIds.Contains(kv.Key) || kv.Value.Camera == null)
                        dead.Add(kv.Key);
                }
                foreach (int id in dead)
                    _blankedCameras.Remove(id);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Eco] BlankWorldCameras warning: {ex.Message}");
        }
    }

    private void RestoreBlankedCameras()
    {
        if (_blankedCameras.Count == 0) return;
        foreach (var kv in _blankedCameras)
        {
            var st = kv.Value;
            try
            {
                if (st.Camera != null)
                {
                    st.Camera.cullingMask = st.OriginalCullingMask;
                    st.Camera.clearFlags = st.OriginalClearFlags;
                    st.Camera.backgroundColor = st.OriginalBackgroundColor;
                }
            }
            catch { }
        }
        _blankedCameras.Clear();
    }

    private PropertyInfo? ResolveTextureMipmapProp()
    {
        if (_textureLimitProbed) return _piTextureLimit;
        _textureLimitProbed = true;
        var qt = typeof(QualitySettings);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        _piTextureLimit = qt.GetProperty("globalTextureMipmapLimit", flags)
                          ?? qt.GetProperty("masterTextureLimit", flags);
        return _piTextureLimit;
    }

    private int GetTextureMipmapLimit()
    {
        try
        {
            var p = ResolveTextureMipmapProp();
            if (p?.GetValue(null) is int v) return v;
        }
        catch { }
        return 0;
    }

    private void SetTextureMipmapLimit(int level)
    {
        try
        {
            var p = ResolveTextureMipmapProp();
            if (p != null && p.CanWrite)
                p.SetValue(null, Math.Clamp(level, 0, 3));
        }
        catch { }
    }

    private PropertyInfo? ResolveAudioVolumeProp()
    {
        if (_audioProbed) return _piAudioVolume;
        _audioProbed = true;
        var t = InteropResolver.FindType("UnityEngine.AudioListener");
        _piAudioVolume = t?.GetProperty("volume", BindingFlags.Public | BindingFlags.Static);
        return _piAudioVolume;
    }

    private float GetAudioVolume()
    {
        try
        {
            var p = ResolveAudioVolumeProp();
            if (p?.GetValue(null) is float f) return f;
        }
        catch { }
        return 1f;
    }

    private void SetAudioMuted(bool muted)
    {
        try
        {
            var p = ResolveAudioVolumeProp();
            if (p != null && p.CanWrite)
                p.SetValue(null, muted ? 0f : _origAudioVolume);
        }
        catch { }
    }

    private void SetProcessEcoPriority(bool eco)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            if (eco)
            {
                if (!_priorityCaptured)
                {
                    _origPriority = proc.PriorityClass;
                    _priorityCaptured = true;
                }
                if (proc.PriorityClass != ProcessPriorityClass.BelowNormal)
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            else if (_priorityCaptured)
            {
                if (proc.PriorityClass != _origPriority)
                    proc.PriorityClass = _origPriority;
                _priorityCaptured = false;
            }
        }
        catch { }
    }

    public JsonObject TrimMemoryNow(bool emptyWorkingSet = true, bool unloadAssets = true)
    {
        long beforeWsMb = 0, beforePrivMb = 0;
        long afterWsMb = 0, afterPrivMb = 0;
        bool emptied = false;
        bool luaGcRan = false;
        bool il2cppGcRan = false;

        try
        {
            using var procBefore = Process.GetCurrentProcess();
            beforeWsMb = procBefore.WorkingSet64 / (1024 * 1024);
            beforePrivMb = procBefore.PrivateMemorySize64 / (1024 * 1024);
        }
        catch { }

        if (unloadAssets)
        {
            try { Resources.UnloadUnusedAssets(); } catch { }
            try
            {
                if (_services.Lua.Ready)
                {
                    _services.Lua.DoString("pcall(collectgarbage, 'collect')");
                    luaGcRan = true;
                }
            }
            catch { }
            try
            {
                Il2CppSystem.GC.Collect();
                il2cppGcRan = true;
            }
            catch { }
            try { GC.Collect(0, GCCollectionMode.Optimized, blocking: false); } catch { }
        }

        if (emptyWorkingSet)
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                emptied = EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        try
        {
            using var procAfter = Process.GetCurrentProcess();
            afterWsMb = procAfter.WorkingSet64 / (1024 * 1024);
            afterPrivMb = procAfter.PrivateMemorySize64 / (1024 * 1024);
        }
        catch { }

        return new JsonObject
        {
            ["ok"] = true,
            ["unloaded_assets"] = unloadAssets,
            ["lua_gc"] = luaGcRan,
            ["il2cpp_gc"] = il2cppGcRan,
            ["emptied_working_set"] = emptied,
            ["before_working_set_mb"] = beforeWsMb,
            ["after_working_set_mb"] = afterWsMb,
            ["before_private_mb"] = beforePrivMb,
            ["after_private_mb"] = afterPrivMb
        };
    }

    public JsonObject GetStatusJson()
    {
        long wsMb = 0, privMb = 0;
        try
        {
            using var p = Process.GetCurrentProcess();
            wsMb = p.WorkingSet64 / (1024 * 1024);
            privMb = p.PrivateMemorySize64 / (1024 * 1024);
        }
        catch { }

        return new JsonObject
        {
            ["mode"] = _config.EcoMode,
            ["effective_mode"] = EffectiveMode,
            ["applied"] = _effectiveApplied,
            ["paused_by_focus"] = _pausedByFocus,
            ["auto_pause_on_focus"] = _config.EcoAutoPauseOnFocus,
            ["relaxed_for_activity"] = _relaxedForActivity,
            ["blanked_cameras"] = _blankedCameras.Count,
            ["working_set_mb"] = wsMb,
            ["private_mb"] = privMb
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreAll();
    }
}

file static class JsonExt
{
    public static JsonObject WithOk(this JsonObject o)
    {
        o["ok"] = true;
        return o;
    }
}
