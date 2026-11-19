using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters.Combat;

namespace Stellar.AgyBridge.World;

/// <summary>
/// Maintains the user/LLM's desired Auto-Combat ('G' key) state persistently across map transitions,
/// loading screens, and temporary follow-catchup sprints.
/// </summary>
public sealed class AutoCombatKeeper
{
    private readonly IPluginServices _services;
    private readonly ICombatAdapter _combat;
    private readonly CrossMapNavigator _navigator;

    private float _settleTimer;
    private float _pollTimer;
    private int _lastSceneId;
    private int _restoreAttemptsInScene;
    private bool _suppressForCatchup;
    private bool _suppressForEvade;

    public bool DesiredEnabled { get; private set; }
    public bool LastKnownInGameEnabled { get; private set; }
    public bool IsSuppressed => _suppressForCatchup || _suppressForEvade;

    public bool SuppressForCatchup
    {
        get => _suppressForCatchup;
        set
        {
            if (_suppressForCatchup == value) return;
            bool wasSuppressed = IsSuppressed;
            _suppressForCatchup = value;
            ApplySuppressionTransition(wasSuppressed);
        }
    }

    public bool SuppressForEvade
    {
        get => _suppressForEvade;
        set
        {
            if (_suppressForEvade == value) return;
            bool wasSuppressed = IsSuppressed;
            _suppressForEvade = value;
            ApplySuppressionTransition(wasSuppressed);
        }
    }

    private void ApplySuppressionTransition(bool wasSuppressed)
    {
        bool nowSuppressed = IsSuppressed;
        if (!wasSuppressed && nowSuppressed)
        {
            // Pause in-game auto-battle immediately so AI doesn't fight catchup sprint, scene/channel hop, or AoE evade
            if (_services.ClientState.Phase == GamePhase.World && _services.PlayerState.IsAvailable)
            {
                _navigator.SetAutoCombat(false);
            }
            LastKnownInGameEnabled = false;
        }
        else if (wasSuppressed && !nowSuppressed && DesiredEnabled)
        {
            // Immediately restore auto-battle once catchup/evade completes
            _pollTimer = 0f;
            _restoreAttemptsInScene = 0;
        }
    }

    public AutoCombatKeeper(IPluginServices services, ICombatAdapter combat, CrossMapNavigator navigator)
    {
        _services = services;
        _combat = combat;
        _navigator = navigator;
    }

    /// <summary>
    /// Marks Auto-Combat as desired from persisted config WITHOUT touching any game object.
    /// Called from the plugin constructor, which runs BEFORE Panda.Core.Game.Init — reading ZEntityMgr.Instance
    /// at that point would instantiate the game's singleton early and break world entry
    /// ("Duplicated manager instance: ZEntityMgr" → NullReferenceException on EnterScene).
    /// <see cref="Tick"/> applies the state once the player is actually in the world.
    /// </summary>
    public void RestoreDesiredFromConfig()
    {
        DesiredEnabled = true;
        _suppressForCatchup = false;
        _suppressForEvade = false;
        _restoreAttemptsInScene = 0;
        _pollTimer = 1.5f;
    }

    public JsonObject SetDesired(bool? enable = null)
    {
        bool curInGame = QueryInGameState();
        bool nextDesired = enable ?? !DesiredEnabled;
        DesiredEnabled = nextDesired;
        _suppressForCatchup = false;
        _suppressForEvade = false;
        _restoreAttemptsInScene = 0;
        _pollTimer = 1.5f;

        var res = _navigator.SetAutoCombat(nextDesired);
        bool afterInGame = QueryInGameState();
        LastKnownInGameEnabled = afterInGame;

        res["desired_enabled"] = DesiredEnabled;
        res["in_game_enabled"] = afterInGame;
        res["persistent_keeper"] = true;
        return res;
    }

    public bool QueryInGameState()
    {
        LastKnownInGameEnabled = _navigator.GetAutoCombatState();
        return LastKnownInGameEnabled;
    }

    public void Tick(float dt, int currentSceneId)
    {
        if (_services.ClientState.Phase != GamePhase.World
            || (_services.ClientState.UiState & GameUIState.Loading) != 0
            || !_services.PlayerState.IsAvailable)
        {
            _settleTimer = 2.0f;
            return;
        }

        if (currentSceneId != _lastSceneId && currentSceneId > 0)
        {
            _lastSceneId = currentSceneId;
            _restoreAttemptsInScene = 0;
            _settleTimer = 2.0f;
        }

        if (_settleTimer > 0f)
        {
            _settleTimer -= dt;
            return;
        }

        if (!DesiredEnabled || IsSuppressed)
            return;

        _pollTimer -= dt;
        if (_pollTimer > 0f) return;
        _pollTimer = 1.5f;

        bool curInGame = QueryInGameState();
        if (curInGame)
        {
            _restoreAttemptsInScene = 0;
            return;
        }

        // Avoid spamming UI notices in safe hub scenes (City=3, Housing=7/8, GuildEventCenter=12000) after 1 check
        bool isSafeHub = IsNonCombatSafeScene(currentSceneId);
        if (isSafeHub && _restoreAttemptsInScene >= 1)
            return;

        if (_restoreAttemptsInScene < 5)
        {
            _restoreAttemptsInScene++;
            _navigator.SetAutoCombat(true);
            bool rechecked = QueryInGameState();
            if (rechecked)
            {
                _services.Log.Info($"[AgyBridge][AutoCombat] Automatically re-enabled Auto-Combat in scene {currentSceneId}.");
            }
        }
    }

    public JsonObject GetStatusJson() => new()
    {
        ["desired_enabled"] = DesiredEnabled,
        ["in_game_enabled"] = LastKnownInGameEnabled,
        ["suppressed_for_catchup"] = _suppressForCatchup,
        ["suppressed_for_evade"] = _suppressForEvade
    };

    private bool IsNonCombatSafeScene(int sceneId) => _combat.IsNonCombatSafeScene(sceneId);
}
