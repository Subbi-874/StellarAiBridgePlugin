using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Core;
using UnityEngine;

namespace Stellar.AgyBridge.Locomotion;

/// <summary>
/// Injects camera-relative movement stick vectors into Panda.ZInput.PlayerInputController
/// using Harmony postfixes owned by Stellar's IHarmonyHost (auto-unpatched on plugin dispose).
/// </summary>
public sealed class PlayerMoveDriver : IDisposable
{
    private static Vector2 InjectStick;
    private static Vector3 InjectWorldDir;
    private static bool InjectActive;
    private static long _lastManualInputTickMs;

    private static void PostfixPrimitive(ref Vector2 __result)
    {
        if (__result.sqrMagnitude > 0.04f)
            Volatile.Write(ref _lastManualInputTickMs, Environment.TickCount64);
        if (InjectActive) __result = InjectStick;
    }

    private static void PostfixInputDir(ref Vector2 __result)
    {
        if (__result.sqrMagnitude > 0.04f)
            Volatile.Write(ref _lastManualInputTickMs, Environment.TickCount64);
        if (InjectActive) __result = InjectStick;
    }

    private static void PostfixGetInputDir(ref Vector3 __result)
    {
        if (__result.sqrMagnitude > 0.04f)
            Volatile.Write(ref _lastManualInputTickMs, Environment.TickCount64);
        if (InjectActive) __result = InjectWorldDir;
    }

    private readonly IPluginServices _services;
    private readonly HashSet<object> _installers = new();
    private object? _owner;
    private Harmony? _harmony;
    private bool _injectPatched;
    private string _injectPath = "none";
    private bool _camWarned;

    private bool _hudResolved;
    private PropertyInfo? _piHudMgrInstance;
    private MethodInfo? _miGetHudMainCamera;

    private PropertyInfo? _piPicInstance;
    private MethodInfo? _miMove;
    private bool _loggedMoveErr;
    private bool _inputReadResolved;
    private PropertyInfo? _piReadPicInstance;
    private MethodInfo? _miReadInputDir;

    public PlayerMoveDriver(IPluginServices services)
    {
        _services = services;
    }

    public string InjectPath => _injectPath;
    public bool IsDriving => InjectActive && _owner != null;

    public double SecondsSinceManualInput
    {
        get
        {
            long ts = Volatile.Read(ref _lastManualInputTickMs);
            return ts <= 0 ? double.PositiveInfinity : Math.Max(0.0, (Environment.TickCount64 - ts) * 0.001);
        }
    }

    public bool IsManualInputRecent(double withinSeconds = 5.0) => SecondsSinceManualInput <= withinSeconds;

    public bool PollManualInputIfIdle()
    {
        if (IsDriving)
            return false;

        try
        {
            if (!_inputReadResolved)
            {
                _inputReadResolved = true;
                var picType = InteropResolver.FindType("Panda.ZInput.PlayerInputController");
                _piReadPicInstance = picType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _miReadInputDir = picType?.GetMethod("get_InputDir", BindingFlags.Public | BindingFlags.Instance);
            }

            var inst = _piReadPicInstance?.GetValue(null);
            if (inst != null && _miReadInputDir?.Invoke(inst, null) is Vector2 vec && vec.sqrMagnitude > 0.04f)
            {
                Volatile.Write(ref _lastManualInputTickMs, Environment.TickCount64);
                return true;
            }
        }
        catch { }
        return false;
    }

    public void Install(object owner)
    {
        _installers.Add(owner);
        InstallInject();
    }

    public void Uninstall(object owner)
    {
        Stop(owner);
        _installers.Remove(owner);
        if (_installers.Count == 0)
            UninstallInject();
    }

    public bool TryDrive(object owner, Vector3 localPos, Vector3 targetPos, float scale = 1.0f, float yawOffsetDegrees = 0f)
    {
        if (!_injectPatched)
            InstallInject();

        if (_owner != null && _owner != owner)
            return false;

        Vector3 wd = targetPos - localPos;
        wd.y = 0f;
        if (wd.sqrMagnitude < 1e-6f)
        {
            Stop(owner);
            return false;
        }
        wd.Normalize();
        if (MathF.Abs(yawOffsetDegrees) > 0.01f)
        {
            wd = Quaternion.Euler(0f, yawOffsetDegrees, 0f) * wd;
            wd.Normalize();
        }

        float sx, sy;
        var cam = GetHudCamera();
        if (cam != null && TryCamAxes(cam, out var fwd, out var right))
        {
            sx = Vector3.Dot(wd, right);
            sy = Vector3.Dot(wd, fwd);
        }
        else
        {
            if (!_camWarned)
            {
                _camWarned = true;
                _services.Log.Info("[AgyBridge][Move] HUD camera not resolved yet; falling back to world axes.");
            }
            sx = wd.x;
            sy = wd.z;
        }

        sx *= scale;
        sy *= scale;

        _owner = owner;
        InjectStick = new Vector2(sx, sy);
        InjectWorldDir = wd;
        InjectActive = true;

        if (_injectPath == "GetInputDir")
            TryMove(InjectStick);

        return true;
    }

    public void Stop(object owner)
    {
        if (_owner != owner) return;
        _owner = null;
        if (!InjectActive) return;
        InjectActive = false;
        InjectStick = Vector2.zero;
        if (_injectPath == "GetInputDir")
            TryMove(Vector2.zero);
    }

    public void ForceStopAll()
    {
        _owner = null;
        if (InjectActive)
        {
            InjectActive = false;
            InjectStick = Vector2.zero;
            if (_injectPath == "GetInputDir")
                TryMove(Vector2.zero);
        }
    }

    public void Dispose()
    {
        ForceStopAll();
        _installers.Clear();
        UninstallInject();
    }

    private static bool TryCamAxes(Camera cam, out Vector3 fwd, out Vector3 right)
    {
        fwd = Vector3.forward;
        right = Vector3.right;
        try
        {
            var t = cam.transform;
            var f = t.forward; f.y = 0f;
            var r = t.right;   r.y = 0f;
            if (f.sqrMagnitude < 1e-6f || r.sqrMagnitude < 1e-6f) return false;
            fwd = f.normalized;
            right = r.normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Camera? GetHudCamera()
    {
        try
        {
            if (!_hudResolved)
            {
                _hudResolved = true;
                var t = InteropResolver.FindType("Panda.Hud.HudMgr");
                _piHudMgrInstance = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _miGetHudMainCamera = t?.GetMethod("GetHudMainCamera", BindingFlags.Public | BindingFlags.Instance);
            }
            var inst = _piHudMgrInstance?.GetValue(null);
            if (inst != null)
            {
                var cam = _miGetHudMainCamera?.Invoke(inst, null) as Camera;
                if (cam != null) return cam;
            }
            return Camera.main;
        }
        catch
        {
            return null;
        }
    }

    private void InstallInject()
    {
        if (_injectPatched) return;
        try
        {
            _harmony ??= _services.Harmony.Create("agybridge.movedriver");
            var picType = InteropResolver.FindType("Panda.ZInput.PlayerInputController");
            if (picType != null)
            {
                var prim = FindNoArg(picType, "getPrimitiveInputDir", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (prim != null && TryPatch(prim, nameof(PostfixPrimitive)))
                {
                    _injectPath = "getPrimitiveInputDir";
                    _injectPatched = true;
                    _services.Log.Info($"[AgyBridge][Move] Patched {_injectPath}");
                    return;
                }

                var getInputDir = picType.GetMethod("get_InputDir", BindingFlags.Public | BindingFlags.Instance);
                if (getInputDir != null && TryPatch(getInputDir, nameof(PostfixInputDir)))
                {
                    _injectPath = "InputDir";
                    _injectPatched = true;
                    _services.Log.Info($"[AgyBridge][Move] Patched {_injectPath}");
                    return;
                }
            }

            var ctrlType = InteropResolver.FindType("Panda.ZGame.CharCtrlCompBase");
            var gid = ctrlType == null ? null : FindNoArg(ctrlType, "GetInputDir", BindingFlags.Public | BindingFlags.Instance);
            if (gid != null && TryPatch(gid, nameof(PostfixGetInputDir)))
            {
                _injectPath = "GetInputDir";
                _injectPatched = true;
                ResolveMoveFacade();
                _services.Log.Info($"[AgyBridge][Move] Patched {_injectPath}");
                return;
            }

            _injectPath = "none";
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Move] InstallInject error: {ex.Message}");
        }
    }

    private void UninstallInject()
    {
        ForceStopAll();
        if (_injectPatched)
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _injectPatched = false;
            _injectPath = "none";
        }
    }

    private bool TryPatch(MethodInfo mi, string postfixName)
    {
        try
        {
            _harmony!.Patch(mi, postfix: new HarmonyMethod(typeof(PlayerMoveDriver), postfixName));
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[AgyBridge][Move] Patch {mi.Name} failed: {ex.Message}");
            return false;
        }
    }

    private static MethodInfo? FindNoArg(Type t, string name, BindingFlags flags)
    {
        foreach (var m in t.GetMethods(flags))
        {
            if (m.Name == name && m.GetParameters().Length == 0)
                return m;
        }
        return null;
    }

    private void ResolveMoveFacade()
    {
        try
        {
            var picType = InteropResolver.FindType("Panda.ZInput.PlayerInputController");
            _piPicInstance = picType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _miMove = picType?.GetMethod("Move", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector2) }, null);
        }
        catch { }
    }

    private void TryMove(Vector2 stick)
    {
        if (_miMove == null || _piPicInstance == null) return;
        try
        {
            var pic = _piPicInstance.GetValue(null);
            if (pic != null) _miMove.Invoke(pic, new object[] { stick });
        }
        catch (Exception ex)
        {
            if (_loggedMoveErr) return;
            _loggedMoveErr = true;
            _services.Log.Warning($"[AgyBridge][Move] Fallback Move error: {ex.Message}");
        }
    }
}
