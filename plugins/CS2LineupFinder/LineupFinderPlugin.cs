using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CS2LineupFinder.Contracts;
using CS2LineupFinder.Plugin.Abstractions;
using CS2LineupFinder.Plugin.Commands;
using CS2LineupFinder.Plugin.Config;
using CS2LineupFinder.Plugin.Data;
using CS2LineupFinder.Plugin.Game;
using CS2LineupFinder.Plugin.Persistence;
using CS2LineupFinder.Plugin.Simulation;
using CS2LineupFinder.Plugin.State;
using CS2LineupFinder.Plugin.Visuals;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using Option = CS2LineupFinder.Plugin.Commands.CommandUsage;

namespace CS2LineupFinder.Plugin;

/// <summary>
/// CounterStrikeSharp entry point of the line-up finder: it owns the service graph,
/// registers the <c>css_lf_*</c> commands and keeps the plugin's state in sync with
/// the map and the players on it.
/// </summary>
/// <remarks>
/// The class is deliberately thin. Everything with a decision in it lives one layer
/// down - <see cref="CommandService"/> for the command behaviour,
/// <see cref="SimulatorBridge"/> for the solve budget, <see cref="GameWorldTracer"/> for
/// collisions - so a handler here only builds a context and calls one method. That is
/// also what makes the acceptance criteria testable without a game server.
///
/// Chat aliases need no code: CounterStrikeSharp rewrites <c>!lf_zone r 200</c> to
/// <c>css_lf_zone r 200</c> and dispatches it here, which is why the usage text in
/// every <see cref="CommandHelperAttribute"/> names the <c>lf_</c> form.
/// </remarks>
public sealed class LineupFinderPlugin : BasePlugin
{
    /// <summary>Directory holding the map catalogue and the saved line-ups.</summary>
    private const string DataDirectory = "data";

    private ServiceGraph? _services;

    private MapCatalog? _catalog;

    private string _loadError = string.Empty;

    /// <inheritdoc />
    public override string ModuleName => "CS2 Line-up Finder";

    /// <inheritdoc />
    public override string ModuleVersion => "0.1.0";

    /// <inheritdoc />
    public override string ModuleAuthor => "CS2LineupFinder contributors";

    /// <inheritdoc />
    public override string ModuleDescription =>
        "Finds grenade line-ups on the map the player is standing on: !lf_menu, !lf_start, !lf_zone, !lf_find.";

    /// <summary>Configuration currently in effect.</summary>
    internal PluginConfig Config { get; private set; } = new();

    /// <inheritdoc />
    public override void Load(bool hotReload)
    {
        try
        {
            var root = string.IsNullOrEmpty(ModuleDirectory) ? AppContext.BaseDirectory : ModuleDirectory;
            Config = PluginConfig.Load(root);

            var services = new ServiceGraph(this, root);
            services.Build();
            _services = services;

            // The timer is the only thing that lets a search running on the thread
            // pool reach the engine, so it is registered before anything can queue
            // work for it.
            AddTickTimer(1, OnTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            services.Dispatcher.CaptureGameThread();

            Logger.LogInformation(
                "CS2LineupFinder loaded: {Config} {Trace} {Core}",
                Config.ToString(),
                services.Tracer.BackendName,
                services.CoreSummary);

            foreach (var problem in Config.UnknownKeys)
            {
                Logger.LogWarning("CS2LineupFinder: config.toml has an unknown key '{Key}'.", problem);
            }

            if (hotReload)
            {
                OnMapStart(Server.MapName);
            }
        }
        catch (Exception ex)
        {
            // A plugin that throws out of Load is unloaded by the host, and then the
            // commands a player types explain nothing. Record the reason, keep the
            // commands registered, and answer with it.
            _loadError = ex.Message;
            Logger.LogError(ex, "CS2LineupFinder failed to start; commands will report the reason.");
        }
    }

    /// <inheritdoc />
    public override void Unload(bool hotReload)
    {
        _services?.Shutdown();
        _services = null;
        _catalog = null;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _services?.Shutdown();
            _services = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>Reads the catalogue for the new map and resets every player's session.</summary>
    /// <param name="mapName">Map that just started.</param>
    [ListenerHandler<Listeners.OnMapStart>]
    public void OnMapStart(string mapName)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var catalog = MapCatalog.Load(services.Root);
            _catalog = catalog;
            services.Commands.MapEnabled = catalog.IsEnabled(mapName);

            // A session is a position on a map, so a map change invalidates all of
            // them; the name and slot stay usable, everything measured does not.
            services.Sessions.ForEach(session =>
            {
                session.Reset();
                session.MapName = mapName;
            });

            services.Beams.Clear();

            foreach (var problem in catalog.Problems)
            {
                Logger.LogWarning("CS2LineupFinder: {Problem}", problem);
            }

            Logger.LogInformation(
                "CS2LineupFinder on {Map}: {Catalog} points={Points} enabled={Enabled}",
                mapName,
                catalog.Describe(),
                catalog.PointsFor(mapName).Count,
                services.Commands.MapEnabled);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CS2LineupFinder could not read the map catalogue for {Map}.", mapName);
        }
    }

    /// <summary>Remembers the name a saved line-up should be credited to.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    /// <param name="name">Persona name.</param>
    /// <param name="ipAddress">Address the player connected from; unused.</param>
    [ListenerHandler<Listeners.OnClientConnect>]
    public void OnClientConnect(int playerSlot, string name, string ipAddress)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            services.Sessions.GetOrCreate(playerSlot, session =>
            {
                session.PlayerName = name ?? string.Empty;
                session.MapName = Server.MapName;
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: could not prepare a session for slot {Slot}: {Message}", playerSlot, ex.Message);
        }
    }

    /// <summary>Refreshes the name after the player has fully spawned.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    [ListenerHandler<Listeners.OnClientPutInServer>]
    public void OnClientPutInServer(int playerSlot)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var live = EnginePlayers.Name(playerSlot);
            services.Sessions.GetOrCreate(playerSlot, session =>
            {
                if (!string.IsNullOrEmpty(live))
                {
                    session.PlayerName = live;
                }

                session.MapName = Server.MapName;
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: could not refresh slot {Slot}: {Message}", playerSlot, ex.Message);
        }
    }

    /// <summary>Drops the session and cancels the search of a player who left.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    [ListenerHandler<Listeners.OnClientDisconnect>]
    public void OnClientDisconnect(int playerSlot)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            services.Cancellations.Cancel(playerSlot);
            services.Sessions.Remove(playerSlot);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: could not clean up slot {Slot}: {Message}", playerSlot, ex.Message);
        }
    }

    /// <summary>Opens the main menu.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_menu", "Opens the line-up finder menu.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnMenuCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, context => OpenMenu(context.PlayerSlot));

    /// <summary>Marks the player's current position as the throw point.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_start", "Marks your current position as the throw point.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnStartCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, static context => context.MarkStart());

    /// <summary>Creates the landing zone around the point the player is aiming at.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_zone", "Creates the landing zone around the point you are looking at.")]
    [CommandHelper(minArgs: 0, usage: "circle <radius> | rect <width> <height>")]
    public void OnZoneCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, static context => context.SetZone());

    /// <summary>Selects the grenade family.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_grenade", "Selects the grenade: smoke, flash, molotov or he.")]
    [CommandHelper(minArgs: 1, usage: "smoke | flash | molotov | he")]
    public void OnGrenadeCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SetGrenade(context.PlayerSlot, context.First, Option.UsageFor("css_lf_grenade")));

    /// <summary>Selects the throw stance.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_throw", "Selects the throw stance: stand, crouch or jump.")]
    [CommandHelper(minArgs: 1, usage: "stand | crouch | jump")]
    public void OnThrowCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SetThrowMode(context.PlayerSlot, context.First, Option.UsageFor("css_lf_throw")));

    /// <summary>Selects the mouse button combination.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_button", "Selects the mouse button: primary, secondary or both.")]
    [CommandHelper(minArgs: 1, usage: "primary | secondary | both")]
    public void OnButtonCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SetButton(context.PlayerSlot, context.First, Option.UsageFor("css_lf_button")));

    /// <summary>Searches for a line-up and shows the result.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_find", "Searches for a line-up and draws the result.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnFindCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => GuardAsync(commandInfo, static context => context.FindAsync());

    /// <summary>Saves the currently selected result under a name.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_save", "Saves the current result under a name.")]
    [CommandHelper(minArgs: 1, usage: "<name>")]
    public void OnSaveCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.SaveLineup(context.PlayerSlot, context.First));

    /// <summary>Loads a saved line-up back into the player's session.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_load", "Loads a saved line-up.")]
    [CommandHelper(minArgs: 1, usage: "<name>")]
    public void OnLoadCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(
        commandInfo,
        static context => context.Service.LoadLineup(context.PlayerSlot, context.First, context.Service.Sessions.GetOrCreate(context.PlayerSlot).MapName));

    /// <summary>Lists the line-ups saved on this map.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_list", "Lists the line-ups saved on this map.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnListCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        var map = context.Service.Sessions.GetOrCreate(context.PlayerSlot).MapName;
        context.Service.ListLineups(context.PlayerSlot, map, context.Config.MaxResults);
    });

    /// <summary>Redraws the aim beam and the impact mark of the last result.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_draw", "Redraws the latest result in the world.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnDrawCommand(CCSPlayerController? player, CommandInfo commandInfo)
        => Guard(commandInfo, static context => context.Service.RedrawLastResult(context.PlayerSlot));

    /// <summary>Picks one of the results of the last search.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_set", "Picks one of the results of the last search.")]
    [CommandHelper(minArgs: 1, usage: "<index>")]
    public void OnSetCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        var slot = context.PlayerSlot;
        var session = context.Service.Sessions.GetOrCreate(slot);
        if (!int.TryParse(context.First, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            context.Service.Reply(slot, Phrases.Id.UnknownOption, context.First ?? string.Empty, Option.UsageFor("css_lf_set"));
            return;
        }

        var count = session.LastOutcome?.Results.Count ?? 0;
        if (index < 1 || index > count)
        {
            context.Service.Reply(slot, Phrases.Id.UnknownOption, index.ToString(CultureInfo.InvariantCulture), Option.UsageFor("css_lf_set"));
            return;
        }

        session.SelectedResultIndex = index - 1;
        context.Service.RedrawLastResult(slot);
    });

    /// <summary>Clears the player's throw point, landing zone and results.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_clear", "Clears the throw point, the landing zone and the results.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnClearCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        context.Service.Clear(context.PlayerSlot);
        context.Service.ClearVisuals();
    });

    /// <summary>Reloads <c>config/config.toml</c> without a server restart.</summary>
    /// <param name="player">Calling player, or <see langword="null"/> for the console.</param>
    /// <param name="commandInfo">Command the player typed.</param>
    [ConsoleCommand("css_lf_reload", "Reloads config.toml.")]
    [CommandHelper(minArgs: 0, usage: CommandSpec.NoArguments)]
    public void OnReloadCommand(CCSPlayerController? player, CommandInfo commandInfo) => Guard(commandInfo, static context =>
    {
        var reloaded = PluginConfig.Load(context.Root);
        context.Service.ApplyConfig(reloaded);
        context.Service.Reply(context.PlayerSlot, Phrases.Id.ConfigReloaded, reloaded.ToString());
    });

    /// <summary>Builds the menu a player sees, so a click always reflects live state.</summary>
    /// <param name="playerSlot">Engine slot of the player.</param>
    private void OpenMenu(int playerSlot)
    {
        var services = _services;
        var player = EnginePlayers.Controller(playerSlot);
        if (services is null || player is null)
        {
            return;
        }

        var config = services.Commands.Config;
        var language = config.Language;
        var session = services.Sessions.GetOrCreate(playerSlot);
        var menu = new ChatMenu(Phrases.Get(language, Phrases.Id.MenuTitle, Phrases.DescribeSession(session, language)));

        // PostSelectAction.Nothing keeps the menu on screen after a click, so a
        // player can set three things in a row without retyping !lf_menu.
        menu.PostSelectAction = PostSelectAction.Nothing;
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuMarkStand), (p, _) => MarkFromMenu(p, ThrowMode.Stand));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuMarkCrouch), (p, _) => MarkFromMenu(p, ThrowMode.Crouch));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuMarkJump), (p, _) => MarkFromMenu(p, ThrowMode.Jump));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuGrenade, Option.Token(session.GrenadeType)), (p, _) => CycleGrenade(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuThrow, Option.Token(session.ThrowMode)), (p, _) => CycleThrowMode(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuButton, Option.Token(session.Button)), (p, _) => CycleButton(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuFind), (p, _) => FindFromMenu(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuSet), (p, _) => services.Commands.Reply(p.Slot, Phrases.Id.MenuSet, Option.Token(session.GrenadeType)), disabled: true);
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuSave), (p, _) => services.Commands.Reply(p.Slot, Phrases.Id.NothingToSave), disabled: true);
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuList), (p, _) => ListFromMenu(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuDraw), (p, _) => services.Commands.RedrawLastResult(p.Slot));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuClear), (p, _) => ClearFromMenu(p));
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MenuReload), (p, _) => ReloadFromMenu(p));

        if (config.VerboseMenu)
        {
            AddDiagnostics(menu, services, config, language);
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void MarkFromMenu(CCSPlayerController player, ThrowMode mode)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var slot = player.Slot;
        services.Sessions.GetOrCreate(slot).ThrowMode = mode;

        var origin = services.Movement.GetThrowOrigin(slot, mode);
        if (origin is null)
        {
            services.Commands.Reply(slot, Phrases.Id.NoPawn);
            return;
        }

        services.Commands.MarkStart(slot, origin.Feet, origin.Eyes, origin.Velocity);
    }

    /// <summary>Adds the settings, backend and path lines the operator asked to see.</summary>
    /// <param name="menu">Menu being built.</param>
    /// <param name="services">Live service graph.</param>
    /// <param name="config">Configuration in effect.</param>
    /// <param name="language">Language the entries are rendered in.</param>
    private static void AddDiagnostics(ChatMenu menu, ServiceGraph services, PluginConfig config, string language)
    {
        // Informational entries. They are disabled so a click cannot be mistaken for
        // an action, which is also why they carry no handler of their own.
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.SearchSettings, config.MaxCandidates, config.SolveTimeoutSeconds, config.MaxResults), static (_, _) => { }, disabled: true);
        menu.AddMenuOption(Phrases.Get(language, Phrases.Id.MapDirectory, services.Lineups.GetMapDirectory(Server.MapName)), static (_, _) => { }, disabled: true);

        if (services.Simulator.UsingStub)
        {
            menu.AddMenuOption(Phrases.Get(language, Phrases.Id.StubSimulator), static (_, _) => { }, disabled: true);
        }

        if (!GameWorldGeometry.CanTrace(services.World))
        {
            menu.AddMenuOption(Phrases.Get(language, Phrases.Id.NoTraceBackend), static (_, _) => { }, disabled: true);
        }
    }

    private void ClearFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        services.Commands.Clear(player.Slot);
        services.Commands.ClearVisuals();
    }

    private void CycleGrenade(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(player.Slot);
        session.GrenadeType = session.GrenadeType switch
        {
            GrenadeType.Smoke => GrenadeType.Flash,
            GrenadeType.Flash => GrenadeType.Molotov,
            GrenadeType.Molotov => GrenadeType.He,
            _ => GrenadeType.Smoke,
        };

        services.Commands.Reply(player.Slot, Phrases.Id.GrenadeSet, Option.Token(session.GrenadeType));
    }

    private void CycleThrowMode(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(player.Slot);
        session.ThrowMode = session.ThrowMode switch
        {
            ThrowMode.Stand => ThrowMode.Crouch,
            ThrowMode.Crouch => ThrowMode.Jump,
            _ => ThrowMode.Stand,
        };

        services.Commands.Reply(player.Slot, Phrases.Id.ThrowModeSet, Option.Token(session.ThrowMode));
    }

    private void CycleButton(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var session = services.Sessions.GetOrCreate(player.Slot);
        session.Button = session.Button switch
        {
            ThrowButton.Primary => ThrowButton.Secondary,
            ThrowButton.Secondary => ThrowButton.Both,
            _ => ThrowButton.Primary,
        };

        services.Commands.Reply(player.Slot, Phrases.Id.ButtonSet, Option.Token(session.Button));
    }

    private void FindFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var slot = player.Slot;
        _ = RunGuardedAsync(
            slot,
            () => services.Commands.FindAsync(slot, services.World, services.Cancellations.TokenFor(slot)));
    }

    private void ListFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        var slot = player.Slot;
        services.Commands.ListLineups(slot, services.Sessions.GetOrCreate(slot).MapName, services.Commands.Config.MaxResults);
    }

    private void ReloadFromMenu(CCSPlayerController player)
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        services.Apply(PluginConfig.Load(services.Root));
        services.Commands.Reply(player.Slot, Phrases.Id.ConfigReloaded, services.Commands.Config.ToString());
    }

    /// <summary>Drains work queued for the game thread; runs once per tick.</summary>
    private void OnTick()
    {
        try
        {
            _services?.Dispatcher.Drain();
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CS2LineupFinder: game thread work failed: {Message}", ex.Message);
        }
    }

    private void Guard(CommandInfo commandInfo, Action<LfCommandContext> body)
    {
        var context = Prepare(commandInfo);
        if (context is null)
        {
            return;
        }

        try
        {
            body(context);
        }
        catch (Exception ex)
        {
            Report(context.PlayerSlot, commandInfo, ex);
        }
    }

    private void GuardAsync(CommandInfo commandInfo, Func<LfCommandContext, Task> body)
    {
        var context = Prepare(commandInfo);
        if (context is null)
        {
            return;
        }

        _ = RunGuardedAsync(context.PlayerSlot, () => body(context));
    }

    private async Task RunGuardedAsync(int playerSlot, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(playerSlot, null, ex);
        }
    }

    /// <summary>
    /// Resolves the calling player and builds a context, or reports why it could not.
    /// </summary>
    /// <param name="commandInfo">Command being handled.</param>
    /// <returns>The context, or <see langword="null"/> when the command cannot run.</returns>
    private LfCommandContext? Prepare(CommandInfo commandInfo)
    {
        var services = _services;
        if (services is null)
        {
            var reason = string.IsNullOrEmpty(_loadError) ? "the plugin is not loaded" : _loadError;
            commandInfo.ReplyToCommand("[LineupFinder] Unavailable: " + reason);
            return null;
        }

        var slot = commandInfo.CallingPlayer?.Slot ?? -1;
        if (slot < 0)
        {
            commandInfo.ReplyToCommand("[LineupFinder] This command runs on a player, not the server console.");
            return null;
        }

        Refresh(services, slot);
        return new LfCommandContext(
            services.Commands,
            services.Movement,
            services.World,
            services.Cancellations,
            services.Apply,
            services.Root,
            slot,
            Args(commandInfo));
    }

    /// <summary>Keeps the session's identity fields current before a command reads them.</summary>
    /// <param name="services">Live service graph.</param>
    /// <param name="playerSlot">Engine slot of the player.</param>
    private static void Refresh(ServiceGraph services, int playerSlot)
    {
        var live = EnginePlayers.Name(playerSlot);
        var map = Server.MapName;
        services.Sessions.GetOrCreate(playerSlot, session =>
        {
            if (!string.IsNullOrEmpty(live))
            {
                session.PlayerName = live;
            }

            if (!string.IsNullOrEmpty(map))
            {
                session.MapName = map;
            }
        });
    }

    private void Report(int playerSlot, CommandInfo? commandInfo, Exception ex)
    {
        Logger.LogError(ex, "CS2LineupFinder: command failed for slot {Slot}.", playerSlot);
        var message = Phrases.Prefixed(
            Config.Language,
            Phrases.Id.SearchFailed,
            ex.GetType().Name,
            ex.Message);

        if (commandInfo is not null)
        {
            commandInfo.ReplyToCommand(message);
            return;
        }

        _services?.Report.Reply(playerSlot, message);
    }

    private static string[] Args(CommandInfo commandInfo)
    {
        var count = Math.Max(commandInfo.ArgCount - 1, 0);
        var arguments = new string[count];
        for (var i = 0; i < count; i++)
        {
            arguments[i] = commandInfo.ArgByIndex(i + 1);
        }

        return arguments;
    }

    /// <summary>
    /// Everything the plugin builds at load time, in one object, so a command can
    /// tell at a glance whether the plugin is running and what it is running with.
    /// </summary>
    private sealed class ServiceGraph
    {
        private readonly LineupFinderPlugin _plugin;

        internal ServiceGraph(LineupFinderPlugin plugin, string root)
        {
            _plugin = plugin;
            Root = root;
            Report = new MessageSink(plugin);
            Dispatcher = new GameThreadDispatcher { OnError = ex => plugin.Logger.LogWarning("CS2LineupFinder: queued work failed: {Message}", ex.Message) };
            Trace = TraceBackendFactory.Create(Report);
            Tracer = new GameWorldTracer(Trace, () => plugin.Config);
            World = new GameWorldGeometry(Tracer) { Config = () => plugin.Config };
            Beams = new BeamRenderer(Dispatcher, () => plugin.Config, Report);
            Visuals = new EngineVisuals(Dispatcher, Beams, () => plugin.Config);
            Sessions = new SessionStore();
            Lineups = new LineupRepository(Path.Combine(root, DataDirectory));
            Simulator = new SimulatorBridge(() => TimeSpan.FromSeconds(plugin.Config.SolveTimeoutSeconds));
            Cancellations = new CancellationRegistry();
            Movement = new GameMovementProvider(Tracer, () => plugin.Config);
            Commands = new CommandService(
                Sessions,
                Lineups,
                Simulator,
                Report,
                Visuals,
                new SystemClock(),
                () => Server.MapName,
                () => plugin.Config,
                config =>
                {
                    plugin.Config = config;
                    return config;
                });
        }

        internal string Root { get; }

        internal MessageSink Report { get; }

        internal GameThreadDispatcher Dispatcher { get; }

        internal ITraceBackend Trace { get; }

        internal GameWorldTracer Tracer { get; }

        internal GameWorldGeometry World { get; }

        internal BeamRenderer Beams { get; }

        internal EngineVisuals Visuals { get; }

        internal SessionStore Sessions { get; }

        internal LineupRepository Lineups { get; }

        internal SimulatorBridge Simulator { get; }

        internal CancellationRegistry Cancellations { get; }

        internal GameMovementProvider Movement { get; }

        internal CommandService Commands { get; }

        /// <summary>One line describing which simulator and solver are in use.</summary>
        internal string CoreSummary { get; private set; } = "built-in stubs";

        /// <summary>Wires the catalogue and looks for the real Core and Math assemblies.</summary>
        internal void Build()
        {
            CoreSummary = CoreLoader.Install(Root, Simulator, Report);
            Commands.MapEnabled = MapCatalog.Load(Root).IsEnabled(Server.MapName);
        }

        /// <summary>Publishes a reloaded configuration to everything that reads it.</summary>
        /// <param name="config">Configuration read from disk.</param>
        /// <returns>The configuration that is now in effect.</returns>
        internal PluginConfig Apply(PluginConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            _plugin.Config = config;
            return config;
        }

        /// <summary>Releases entities and parked work at unload time.</summary>
        internal void Shutdown()
        {
            Cancellations.CancelAll();
            Dispatcher.Post(Beams.Clear);
            Dispatcher.Shutdown();
            Beams.Dispose();
            Visuals.Dispose();
            Cancellations.Dispose();
        }
    }

    /// <summary>Routes the command layer's chat and log output into the running server.</summary>
    private sealed class MessageSink : IPluginMessages
    {
        private readonly LineupFinderPlugin _plugin;

        internal MessageSink(LineupFinderPlugin plugin) => _plugin = plugin;

        /// <inheritdoc />
        public void Reply(int playerSlot, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var player = EnginePlayers.Controller(playerSlot);
            if (player is not null)
            {
                player.PrintToChat(text);
                return;
            }

            // No player behind the slot: the console has to hear it instead.
            _plugin.Logger.LogInformation("CS2LineupFinder (slot {Slot}): {Text}", playerSlot, Strip(text));
        }

        /// <inheritdoc />
        public void Log(string text) => _plugin.Logger.LogInformation("{Text}", Strip(text));

        /// <inheritdoc />
        public void LogWarning(string text) => _plugin.Logger.LogWarning("{Text}", Strip(text));

        /// <summary>Console output does not need the chat colour codes the messages carry.</summary>
        private static string Strip(string text) => text.Replace('\u0001', ' ').Replace('\u0003', ' ').Trim();
    }
}
