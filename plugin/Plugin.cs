using System;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;
using Stellar.AgyBridge.Behaviors;
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
    private readonly BehaviorRunner _behaviors;
    private readonly CommandRegistry _commands;
    private readonly BridgeWebSocketClient _wsClient;
    private readonly AgyBridgeUiWindow _uiWindow;
    private readonly IUpdateRateScope _rateScope;
    private float _bgCheckTimer;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _config = new BridgeConfig(_services.Config);
        _dispatcher = new MainThreadDispatcher(_services.Log);
        _lua = new LuaBridge(_services);
        _moveDriver = new PlayerMoveDriver(_services);
        _boost = new LocomotionBoostController(_services, _config, _lua);
        _world = new WorldStateService(_services, _config, _lua);
        _navigator = new CrossMapNavigator(_services, _lua);
        _autoCombat = new AutoCombatKeeper(_services, _lua, _navigator);
        if (_config.IsFeatureEnabled(FeaturePolicyKeys.AutoCombat))
        {
            // Never touch game singletons from the constructor (see AutoCombatKeeper.RestoreDesiredFromConfig).
            _autoCombat.RestoreDesiredFromConfig();
        }
        _party = new PartyManagerService(_services, _lua, _world);
        _autoGather = new AutoGatherIntegration(_services, _lua);
        _behaviors = new BehaviorRunner(_services, _moveDriver);
        _commands = new CommandRegistry();

        _asyncTasks = new AsyncTaskRegistry();
        _revive = new ReviveService(_services, _config, _lua, _world);
        _mount = new MountService(_services, _lua);
        _interaction = new InteractionInviteService(_services, _config, _lua, _world);
        _sender = new RequestSenderService(_services, _lua, _world);
        _locator = new PlayerLocatorService(_services, _lua, _world, _navigator);
        _loadouts = new LoadoutPresetService(_services, _lua, _asyncTasks);
        _outfits = new OutfitPresetService(_services, _lua, _asyncTasks, _dispatcher);
        _monsters = new MonsterLocator(_lua, _world);
        _autoReady = new AutoReadyService(_services, _config, _lua, _world);
        _dungeonEntry = new DungeonEntryService(_services, _lua, _party);
        _stickers = new StickerService(_services, _lua, _world);
        _autoFishing = new AutoFishingIntegration(_services, _lua);
        _traversal = new TraversalGimmickService(_services, _lua, _mount);
        _combatEvade = new CombatEvadeService(
            _services,
            _config,
            _lua,
            _autoCombat,
            _moveDriver,
            _boost,
            new ZPhysicsSensor(_services),
            () => _behaviors.Current);
        _cameraGuard = new AutoCameraGuardService(_services, _config, _world, _autoCombat, _behaviors);

        _wsClient = new BridgeWebSocketClient(_services.Log, _config, _dispatcher, _commands);
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
            _wsClient);

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
            _uiWindow.MarkDirty);

        _world.SubscribeEvents(_wsClient.PushEvent);
        _config.ChatSettingsChanged += OnUiChatSettingsChanged;
        _config.FeatureModeChanged += OnFeatureModeChanged;

        EnforceRunInBackground();

        // Request 60Hz ticking when user allows rate control so locomotion & bunnyhop timing stay crisp
        _rateScope = _services.Framework.RequestUpdateRate(60);
        _services.Framework.Update += OnUpdate;

        _wsClient.Start();
        _services.Log.Info($"[AgyBridge] Initialized v0.9.15. Connecting to {_config.BridgeUrl}");
    }

    private void OnFeatureModeChanged(string key, FeatureGateMode mode)
    {
        if (key == FeaturePolicyKeys.AutoCombat)
        {
            _autoCombat.SetDesired(mode == FeatureGateMode.Enabled);
        }
        else if (key == FeaturePolicyKeys.FollowMove && mode != FeatureGateMode.Enabled)
        {
            if (_behaviors.Current is FollowPartyBehavior or MoveToBehavior)
                _behaviors.StopCurrent("policy_disabled_or_locked");
        }
        else if (key == FeaturePolicyKeys.AutoGather && mode != FeatureGateMode.Enabled)
        {
            if (_behaviors.Current is GatherResourceBehavior)
                _behaviors.StopCurrent("policy_disabled_or_locked");
            _autoGather.StopAutoGather();
        }
        else if (key == FeaturePolicyKeys.AutoFish && mode != FeatureGateMode.Enabled)
        {
            if (_behaviors.Current is FishAtSpotBehavior)
                _behaviors.StopCurrent("policy_disabled_or_locked");
            _autoFishing.StopAutoFishing();
            _autoFishing.ExitFishingStateLua();
        }
        else if (key == FeaturePolicyKeys.Emote && mode != FeatureGateMode.Enabled)
        {
            _world.ForceResetActiveEmote();
        }
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
        _behaviors.Tick(deltaTime);
        _combatEvade.Tick(deltaTime);
        _boost.TickReleasesOnly(deltaTime);
        _autoCombat.Tick(deltaTime, _world.GetLocalSceneId());
        _cameraGuard.Tick(deltaTime);
        _revive.Tick(deltaTime);
        _interaction.Tick(deltaTime);
        _autoReady.Tick(deltaTime);
        _uiWindow.TickPendingEdits();
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
        _config.FeatureModeChanged -= OnFeatureModeChanged;
        _config.ChatSettingsChanged -= OnUiChatSettingsChanged;
        _world.UnsubscribeEvents(_wsClient.PushEvent);
        _autoReady.Dispose();
        _uiWindow.Dispose();
        _wsClient.Dispose();
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
