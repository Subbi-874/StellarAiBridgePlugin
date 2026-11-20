using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Adapters;
using Stellar.AgyBridge.Adapters.Character;
using Stellar.AgyBridge.Adapters.Combat;
using Stellar.AgyBridge.Adapters.Dungeon;
using Stellar.AgyBridge.Adapters.External;
using Stellar.AgyBridge.Adapters.Items;
using Stellar.AgyBridge.Adapters.Movement;
using Stellar.AgyBridge.Adapters.Social;
using Stellar.AgyBridge.Behaviors;
using Stellar.AgyBridge.Bridge.Server;
using Stellar.AgyBridge.Character;
using Stellar.AgyBridge.Commands;
using Stellar.AgyBridge.Core;
using Stellar.AgyBridge.Fishing;
using Stellar.AgyBridge.Gathering;
using Stellar.AgyBridge.Locomotion;
using Stellar.AgyBridge.Lua;
using Stellar.AgyBridge.Network;
using Stellar.AgyBridge.UI;
using Stellar.AgyBridge.World;
using UnityEngine;

namespace Stellar.AgyBridge;

public sealed class Plugin : IStellarPlugin
{
    public string Name => "AGY Bridge";

    private readonly IPluginServices _services;
    private readonly BridgeConfig _config;
    private readonly MainThreadDispatcher _dispatcher;
    private readonly LuaBridge _lua;
    private readonly WorldAccessGuard _worldGuard;
    private readonly CapabilityRegistry _capabilities;
    private readonly ICombatAdapter _combatAdapter;
    private readonly ICharacterAdapter _characterAdapter;
    private readonly IDungeonAdapter _dungeonAdapter;
    private readonly ISocialAdapter _socialAdapter;
    private readonly IExternalPluginAdapter _externalPluginAdapter;
    private readonly IItemAdapter _itemAdapter;
    private readonly IMovementAdapter _movementAdapter;
    private readonly PlayerMoveDriver _moveDriver;
    private readonly LocomotionBoostController _boost;
    private readonly WorldStateService _world;
    private readonly CrossMapNavigator _navigator;
    private readonly AutoCombatKeeper _autoCombat;
    private readonly PartyManagerService _party;
    private readonly AutoGatherIntegration _autoGather;
    private readonly AsyncTaskRegistry _asyncTasks;
    private readonly ReviveService _revive;
    private readonly MountService _mount;
    private readonly InteractionInviteService _interaction;
    private readonly RequestSenderService _sender;
    private readonly PlayerLocatorService _locator;
    private readonly LoadoutPresetService _loadouts;
    private readonly OutfitPresetService _outfits;
    private readonly MonsterLocator _monsters;
    private readonly AutoReadyService _autoReady;
    private readonly DungeonEntryService _dungeonEntry;
    private readonly StickerService _stickers;
    private readonly AutoFishingIntegration _autoFishing;
    private readonly TraversalGimmickService _traversal;
    private readonly CombatEvadeService _combatEvade;
    private readonly AutoCameraGuardService _cameraGuard;
    private readonly EcoModeService _ecoMode;
    private readonly BehaviorRunner _behaviors;
    private readonly CommandRegistry _commands;
    private readonly LeaderBridgeHost _bridgeHost;
    private readonly BridgeWebSocketClient _wsClient;
    private readonly AgyBridgeUiWindow _uiWindow;
    private sealed record IdentitySnapshot(long CharId, string CharName, bool InWorld);

    private readonly IUpdateRateScope _rateScope;
    private volatile IdentitySnapshot _identity = new(0L, "", false);
    private float _bgCheckTimer;
    private float _identityCheckTimer;
    private float _manualInputPollTimer;
    private float _manualInputEventCooldown;
    private bool _wasWsConnected;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _config = new BridgeConfig(_services.Config, _services.Localization);
        _dispatcher = new MainThreadDispatcher(_services.Log);
        _lua = new LuaBridge(_services);
        _worldGuard = new WorldAccessGuard(_services, _lua);
        _combatAdapter = new LuaCombatAdapter(_services, _lua, _worldGuard);
        _characterAdapter = new LuaCharacterAdapter(_services, _lua, _worldGuard);
        _dungeonAdapter = new LuaDungeonAdapter(_services, _lua, _worldGuard);
        _socialAdapter = new LuaSocialAdapter(_services, _lua, _worldGuard);
        _externalPluginAdapter = new ExternalPluginAdapter(_services, _lua, _worldGuard);
        _itemAdapter = new LuaItemAdapter(_services, _lua, _worldGuard);
        _movementAdapter = new LuaMovementAdapter(_services, _lua, _worldGuard);
        _capabilities = new CapabilityRegistry(_services.Log, _worldGuard, _services.Localization);
        _capabilities.RegisterProbe(_combatAdapter);
        _capabilities.RegisterProbe(_characterAdapter);
        _capabilities.RegisterProbe(_dungeonAdapter);
        _capabilities.RegisterProbe(_socialAdapter);
        _capabilities.RegisterProbe(_externalPluginAdapter);
        _capabilities.RegisterProbe(_itemAdapter);
        _capabilities.RegisterProbe(_movementAdapter);
        _capabilities.RunLoadTimeProbes();
        _moveDriver = new PlayerMoveDriver(_services);
        _boost = new LocomotionBoostController(_services, _config, _movementAdapter);
        _world = new WorldStateService(_services, _config, _socialAdapter);
        _navigator = new CrossMapNavigator(_services, _movementAdapter, _combatAdapter);
        _autoCombat = new AutoCombatKeeper(_services, _combatAdapter, _navigator);
        if (_config.IsFeatureEnabled(FeaturePolicyKeys.AutoCombat))
        {
            // Never touch game singletons from the constructor (see AutoCombatKeeper.RestoreDesiredFromConfig).
            _autoCombat.RestoreDesiredFromConfig();
        }
        _party = new PartyManagerService(_services, _socialAdapter, _world);
        _autoGather = new AutoGatherIntegration(_externalPluginAdapter);
        _behaviors = new BehaviorRunner(_services, _moveDriver);
        _commands = new CommandRegistry();
        _commands.SetPreDispatchGuard(_capabilities.CheckCommandCapability);

        _asyncTasks = new AsyncTaskRegistry();
        _revive = new ReviveService(_services, _config, _combatAdapter, _world);
        _mount = new MountService(_services, _characterAdapter);
        _interaction = new InteractionInviteService(_services, _config, _socialAdapter, _world);
        _sender = new RequestSenderService(_socialAdapter, _world);
        _locator = new PlayerLocatorService(_services, _socialAdapter, _world, _navigator);
        _loadouts = new LoadoutPresetService(_services, _characterAdapter, _asyncTasks);
        _outfits = new OutfitPresetService(_services, _characterAdapter, _asyncTasks, _dispatcher);
        _monsters = new MonsterLocator(_characterAdapter, _world);
        _autoReady = new AutoReadyService(_services, _config, _dungeonAdapter, _world);
        _dungeonEntry = new DungeonEntryService(_services, _dungeonAdapter, _party);
        _stickers = new StickerService(_services, _socialAdapter, _world);
        _autoFishing = new AutoFishingIntegration(_services, _externalPluginAdapter);
        _traversal = new TraversalGimmickService(_services, _movementAdapter, _mount);
        _combatEvade = new CombatEvadeService(
            _services,
            _config,
            _combatAdapter,
            _autoCombat,
            _moveDriver,
            _boost,
            new ZPhysicsSensor(_services),
            () => _behaviors.Current);
        _cameraGuard = new AutoCameraGuardService(_services, _config, _world, _autoCombat, _behaviors);
        _ecoMode = new EcoModeService(_services, _config);

        _bridgeHost = new LeaderBridgeHost(
            () => _config.BridgeUrl,
            () => _config.AutoStartLocalBridge,
            () => _config.AuthToken,
            log: msg => _services.Log.Info(msg));
        _wsClient = new BridgeWebSocketClient(_services.Log, _config, _dispatcher, _commands, _capabilities.BuildStatusJson, BuildIdentitySnapshotJson, _bridgeHost);
        _uiWindow = new AgyBridgeUiWindow(
            _services,
            _config,
            _world,
            _navigator,
            _autoCombat,
            _moveDriver,
            _boost,
            _behaviors,
            _party,
            _autoGather,
            _traversal,
            _wsClient,
            _capabilities,
            _ecoMode);

        CoreCommands.RegisterAll(
            _commands,
            _services,
            _config,
            _lua,
            _moveDriver,
            _boost,
            _world,
            _navigator,
            _autoCombat,
            _behaviors,
            _party,
            _autoGather,
            _autoFishing,
            _locator,
            _asyncTasks,
            _combatEvade,
            _traversal,
            _cameraGuard,
            _capabilities,
            _ecoMode,
            _uiWindow.MarkDirty);

        ExtendedCommands.RegisterAll(
            _commands,
            _services,
            _config,
            _world,
            _navigator,
            _autoCombat,
            _moveDriver,
            _boost,
            _behaviors,
            _revive,
            _mount,
            _interaction,
            _sender,
            _locator,
            _loadouts,
            _outfits,
            _monsters,
            _autoReady,
            _dungeonEntry,
            _stickers,
            _autoFishing,
            _traversal,
            _itemAdapter,
            _lua,
            _uiWindow.MarkDirty);

        _world.SubscribeEvents(_wsClient.PushEvent);
        _behaviors.BehaviorFinished += OnBehaviorFinished;
        _capabilities.CapabilitiesUpdated += OnCapabilitiesUpdated;
        _config.ChatSettingsChanged += OnUiChatSettingsChanged;
        _services.Localization.LanguageChanged += OnUiChatSettingsChanged;
        _config.FeaturePolicyChanged += OnFeaturePolicyChanged;

        EnforceRunInBackground();

        // Request 60Hz ticking when user allows rate control so locomotion & bunnyhop timing stay crisp
        _rateScope = _services.Framework.RequestUpdateRate(60);
        _services.Framework.Update += OnUpdate;

        _wsClient.Start();
        _services.Log.Info($"[AgyBridge] Initialized v1.0.3. Connecting to {_config.BridgeUrl}");
    }

    private void OnFeaturePolicyChanged(string key, FeaturePolicyState policy)
    {
        if (key == FeaturePolicyKeys.AutoCombat)
        {
            _autoCombat.SetDesired(policy.Enabled);
            if (!policy.Enabled && _behaviors.Current is FarmMonsterItemBehavior)
                _behaviors.StopCurrent("policy_disabled");
        }
        else if (key == FeaturePolicyKeys.FollowMove && !policy.Enabled)
        {
            if (_behaviors.Current is FollowPartyBehavior or MoveToBehavior or SeekMonsterBehavior or UseGimmickBehavior or FarmMonsterItemBehavior)
                _behaviors.StopCurrent("policy_disabled");
        }
        else if (key == FeaturePolicyKeys.AutoCraft && !policy.Enabled)
        {
            if (_behaviors.Current is CraftItemBehavior)
                _behaviors.StopCurrent("policy_disabled");
        }
        else if (key == FeaturePolicyKeys.AutoGather && !policy.Enabled)
        {
            if (_behaviors.Current is GatherResourceBehavior)
                _behaviors.StopCurrent("policy_disabled");
            _autoGather.StopAutoGather();
        }
        else if (key == FeaturePolicyKeys.AutoFish && !policy.Enabled)
        {
            if (_behaviors.Current is FishAtSpotBehavior)
                _behaviors.StopCurrent("policy_disabled");
            _autoFishing.StopAutoFishing();
            _autoFishing.ExitFishingStateLua();
        }
        else if (key == FeaturePolicyKeys.Emote && !policy.Enabled)
        {
            _world.ForceResetActiveEmote();
        }
        _uiWindow.MarkDirty();
    }

    private void OnCapabilitiesUpdated()
    {
        _wsClient.PushEvent(new JsonObject
        {
            ["type"] = "event",
            ["event"] = "capabilities_updated",
            ["data"] = _capabilities.BuildStatusJson()
        });
        _uiWindow.MarkDirty();
    }

    private void OnUiChatSettingsChanged()
    {
        _wsClient.PushEvent(new JsonObject
        {
            ["type"] = "event",
            ["event"] = "ui_chat_settings_changed",
            ["data"] = _config.ToChatSettingsJson()
        });
    }

    private void OnUpdate(float deltaTime)
    {
        _bgCheckTimer -= deltaTime;
        if (_bgCheckTimer <= 0f)
        {
            _bgCheckTimer = 2.0f;
            EnforceRunInBackground();
        }

        _dispatcher.Drain();
        _capabilities.Tick(deltaTime);
        _behaviors.Tick(deltaTime);
        TickIdentitySync(deltaTime);
        TickManualInputDetection(deltaTime);
        _combatEvade.Tick(deltaTime);
        _boost.TickReleasesOnly(deltaTime);
        _autoCombat.Tick(deltaTime, _world.GetLocalSceneId());
        _cameraGuard.Tick(deltaTime);
        _revive.Tick(deltaTime);
        _interaction.Tick(deltaTime);
        _autoReady.Tick(deltaTime);
        bool ecoActivityActive = _behaviors.Current != null || _moveDriver.IsDriving || _autoCombat.LastKnownInGameEnabled;
        _ecoMode.Tick(deltaTime, ecoActivityActive, _identity.InWorld);
        _uiWindow.TickPendingEdits();
    }

    private JsonObject BuildIdentitySnapshotJson()
    {
        var id = _identity;
        return new JsonObject
        {
            ["local_char_id"] = id.CharId,
            ["local_char_name"] = id.CharName,
            ["in_world"] = id.InWorld
        };
    }

    private void OnBehaviorFinished(string kind, string reason, JsonObject snap)
    {
        var id = _identity;
        var clonedSnap = JsonNode.Parse(snap.ToJsonString()) ?? new JsonObject();
        _world.EmitBridgeEvent("behavior_finished", new JsonObject
        {
            ["kind"] = kind,
            ["reason"] = reason,
            ["phase"] = snap["phase"]?.GetValue<string>() ?? reason,
            ["local_char_id"] = id.CharId,
            ["local_char_name"] = id.CharName,
            ["snapshot"] = clonedSnap
        });
    }

    private void TickIdentitySync(float deltaTime)
    {
        bool wsConnected = _wsClient.IsConnected;
        bool reconnectEdge = wsConnected && !_wasWsConnected;
        _wasWsConnected = wsConnected;

        _identityCheckTimer -= deltaTime;
        if (!reconnectEdge && _identityCheckTimer > 0f)
            return;
        _identityCheckTimer = 0.25f;

        bool inWorld = _services.ClientState.Phase == GamePhase.World && _services.PlayerState.IsAvailable;
        long cid = inWorld ? _services.PlayerState.CharId : 0L;
        string cname = inWorld ? (_services.PlayerState.Name ?? "") : "";

        var prev = _identity;
        bool changed = cid != prev.CharId || (cid > 0 && cname != prev.CharName) || inWorld != prev.InWorld;
        if (changed)
            _identity = new IdentitySnapshot(cid, cname, inWorld);

        if (wsConnected && (changed || (reconnectEdge && cid > 0)))
        {
            _wsClient.PushEvent(new JsonObject
            {
                ["type"] = "event",
                ["event"] = "bot_identity_changed",
                ["data"] = new JsonObject
                {
                    ["local_char_id"] = cid,
                    ["local_char_name"] = cname,
                    ["in_world"] = inWorld,
                    ["previous_char_id"] = prev.CharId
                }
            });
        }
    }

    private void TickManualInputDetection(float deltaTime)
    {
        if (_manualInputEventCooldown > 0f)
            _manualInputEventCooldown = MathF.Max(0f, _manualInputEventCooldown - deltaTime);

        var id = _identity;
        if (!id.InWorld)
            return;

        _manualInputPollTimer -= deltaTime;
        if (_manualInputPollTimer <= 0f)
        {
            _manualInputPollTimer = 0.15f;
            _moveDriver.PollManualInputIfIdle();
        }

        if (_manualInputEventCooldown <= 0f && _moveDriver.IsManualInputRecent(0.4))
        {
            _manualInputEventCooldown = 3.0f;
            _world.EmitBridgeEvent("manual_input_detected", new JsonObject
            {
                ["local_char_id"] = id.CharId,
                ["local_char_name"] = id.CharName,
                ["is_driving"] = _moveDriver.IsDriving
            });
        }
    }

    private void EnforceRunInBackground()
    {
        if (!_config.KeepRunningInBackground) return;
        try
        {
            if (!Application.runInBackground)
                Application.runInBackground = true;
        }
        catch (Exception)
        {
            // Ignore if Unity Application property isn't ready yet
        }
    }

    public void Dispose()
    {
        _services.Framework.Update -= OnUpdate;
        _config.FeaturePolicyChanged -= OnFeaturePolicyChanged;
        _services.Localization.LanguageChanged -= OnUiChatSettingsChanged;
        _config.ChatSettingsChanged -= OnUiChatSettingsChanged;
        _capabilities.CapabilitiesUpdated -= OnCapabilitiesUpdated;
        _behaviors.BehaviorFinished -= OnBehaviorFinished;
        _world.UnsubscribeEvents(_wsClient.PushEvent);
        _autoReady.Dispose();
        _uiWindow.Dispose();
        _ecoMode.Dispose();
        _wsClient.Dispose();
        _bridgeHost.Dispose();
        _cameraGuard.Dispose();
        _combatEvade.Dispose();
        _boost.Reset();
        _behaviors.Dispose();
        _world.Dispose();
        _moveDriver.Dispose();
        _rateScope.Dispose();
        _services.Log.Info("[AgyBridge] Disposed.");
    }
}
