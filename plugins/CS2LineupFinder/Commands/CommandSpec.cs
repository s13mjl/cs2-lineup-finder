using System;
using System.Collections.Generic;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// The command surface of the plugin in one table: the console name, the chat
/// alias and the help text.
/// </summary>
/// <remarks>
/// The table exists because the help text is part of the acceptance criteria, not a
/// decoration. CounterStrikeSharp renders it as
/// <c>Usage: "&lt;chat alias&gt; &lt;usage&gt;"</c> whenever a command is used
/// wrongly, so an empty entry would silently print a bare command name. Keeping the
/// table free of engine types means a unit test can assert that every entry has one.
/// </remarks>
public static class CommandSpec
{
    /// <summary>Console command prefix, the one CounterStrikeSharp registers.</summary>
    public const string ConsolePrefix = "css_";

    /// <summary>Chat alias prefix, so <c>!lf_menu</c> reaches <c>css_lf_menu</c>.</summary>
    public const string ChatPrefix = "lf_";

    /// <summary>Usage text of a command that needs no arguments.</summary>
    public const string NoArguments = "no arguments";

    /// <summary>Every command the plugin registers, in the order the menu lists them.</summary>
    public static readonly IReadOnlyList<Spec> All = new Spec[]
    {
        new("css_lf_menu", "lf_menu", NoArguments, "Opens the line-up finder menu."),
        new("css_lf_start", "lf_start", NoArguments, "Marks your current position as the throw point."),
        new("css_lf_zone", "lf_zone", "circle <radius> | rect <width> <height>", "Creates the landing zone around the point you are looking at."),
        new("css_lf_grenade", "lf_grenade", "smoke | flash | molotov | he", "Selects the grenade."),
        new("css_lf_throw", "lf_throw", "stand | crouch | jump", "Selects the throw stance."),
        new("css_lf_button", "lf_button", "primary | secondary | both", "Selects the mouse button combination."),
        new("css_lf_find", "lf_find", NoArguments, "Searches for a line-up and draws the result."),
        new("css_lf_save", "lf_save", "<name>", "Saves the current result under a name."),
        new("css_lf_load", "lf_load", "<name>", "Loads a saved line-up."),
        new("css_lf_list", "lf_list", NoArguments, "Lists the line-ups saved on this map."),
        new("css_lf_draw", "lf_draw", NoArguments, "Redraws the latest result in the world."),
        new("css_lf_set", "lf_set", "<index>", "Picks one of the results of the last search."),
        new("css_lf_clear", "lf_clear", NoArguments, "Clears the throw point, the landing zone and the results."),
        new("css_lf_reload", "lf_reload", NoArguments, "Reloads config.toml."),
    };

    /// <summary>Chat aliases, in the same order as <see cref="All"/>.</summary>
    public static readonly IReadOnlyList<string> ChatAliases = BuildAliases();

    /// <summary>Returns the usage text of a console command.</summary>
    /// <param name="consoleName">Command name including the <c>css_</c> prefix.</param>
    /// <returns>The usage text, or an empty string when the command is unknown.</returns>
    public static string UsageOf(string consoleName)
    {
        foreach (var spec in All)
        {
            if (string.Equals(spec.Console, consoleName, StringComparison.OrdinalIgnoreCase))
            {
                return spec.Usage;
            }
        }

        return string.Empty;
    }

    /// <summary>Finds the entry for a console command.</summary>
    /// <param name="consoleName">Command name including the <c>css_</c> prefix.</param>
    /// <returns>The entry, or <see langword="null"/> when the command is unknown.</returns>
    public static Spec? Find(string consoleName)
    {
        foreach (var spec in All)
        {
            if (string.Equals(spec.Console, consoleName, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildAliases()
    {
        var aliases = new string[All.Count];
        for (var i = 0; i < All.Count; i++)
        {
            aliases[i] = All[i].Chat;
        }

        return aliases;
    }

    /// <summary>One command: the console name, the chat alias and the help text.</summary>
    /// <param name="Console">Name registered with CounterStrikeSharp, e.g. <c>css_lf_zone</c>.</param>
    /// <param name="Chat">Alias that reaches it from chat, without the trigger character.</param>
    /// <param name="Usage">Argument summary only, without the command name.</param>
    /// <param name="Help">One line describing what the command does.</param>
    public readonly record struct Spec(string Console, string Chat, string Usage, string Help)
    {
        /// <summary>True when the command takes no arguments.</summary>
        public bool TakesNoArguments => Usage == NoArguments;

        /// <summary>Renders the usage as CounterStrikeSharp prints it, for tests and docs.</summary>
        /// <returns>The full usage line.</returns>
        public string FullUsage => Chat + " " + Usage;
    }
}
