using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CS2LineupFinder.Plugin;
using CS2LineupFinder.Plugin.Commands;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// The command surface as CounterStrikeSharp sees it. The brief asks every
/// command to carry help text, and the only way to prove that for the real
/// registration path is to reflect over the plugin class.
/// </summary>
public sealed class RegistrationTests
{
    private static IReadOnlyList<MethodInfo> Handlers(Type pluginType) => pluginType
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => method.GetCustomAttribute<ConsoleCommandAttribute>() is not null)
        .ToArray();

    [Fact]
    public void EveryCommand_IsRegisteredWithHelpText()
    {
        var handlers = Handlers(typeof(LineupFinderPlugin));
        Assert.NotEmpty(handlers);

        var names = new List<string>();
        foreach (var handler in handlers)
        {
            var command = handler.GetCustomAttribute<ConsoleCommandAttribute>()!;
            var helper = handler.GetCustomAttribute<CommandHelperAttribute>();

            Assert.True(
                helper is not null,
                $"{command.Command} has no [CommandHelper], so CounterStrikeSharp cannot print a usage line for it.");
            Assert.False(
                string.IsNullOrWhiteSpace(helper!.Usage),
                $"{command.Command} has an empty usage string.");
            Assert.False(
                string.IsNullOrWhiteSpace(command.Description),
                $"{command.Command} has an empty description.");
            Assert.StartsWith(CommandSpec.ConsolePrefix, command.Command, StringComparison.Ordinal);
            names.Add(command.Command);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RegisteredCommands_AndTheSpecTable_AgreeExactly()
    {
        var registered = Handlers(typeof(LineupFinderPlugin))
            .Select(method => method.GetCustomAttribute<ConsoleCommandAttribute>()!.Command)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var documented = CommandSpec.All
            .Select(spec => spec.Console)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(documented, registered);
    }

    [Fact]
    public void HelpText_InTheAttribute_MatchesTheSpecTable()
    {
        foreach (var handler in Handlers(typeof(LineupFinderPlugin)))
        {
            var command = handler.GetCustomAttribute<ConsoleCommandAttribute>()!;
            var helper = handler.GetCustomAttribute<CommandHelperAttribute>()!;
            var spec = CommandSpec.Find(command.Command);

            Assert.True(spec is not null, $"{command.Command} is missing from CommandSpec.All.");
            Assert.Equal(spec!.Value.Usage, helper.Usage);
            Assert.False(string.IsNullOrWhiteSpace(spec.Value.Help));
            if (spec.Value.TakesNoArguments)
            {
                // "no arguments" in the table has to mean what it says, otherwise the
                // help line contradicts what the engine lets the player type.
                Assert.Equal(0, helper.MinArgs);
            }
        }
    }

    [Fact]
    public void EverySpec_IsReachableFromChat()
    {
        Assert.Equal("css_", CommandSpec.ConsolePrefix);
        Assert.Equal("lf_", CommandSpec.ChatPrefix);
        Assert.Equal("!lf_", CS2LineupFinder.Plugin.Commands.CommandUsage.ChatPrefix);
        Assert.Equal(CommandSpec.All.Count, CommandSpec.ChatAliases.Count);

        foreach (var spec in CommandSpec.All)
        {
            // CounterStrikeSharp resolves !lf_menu to css_lf_menu because the chat alias
            // is the console name with the css_ prefix swapped for !.
            Assert.Equal(spec.Console[CommandSpec.ConsolePrefix.Length..], spec.Chat);
            Assert.Equal(spec.Chat, CS2LineupFinder.Plugin.Commands.CommandUsage.CommandNameFromChat("!" + spec.Chat));
            // CounterStrikeSharp also declares a CommandUsage type, so name ours in full.
            Assert.Equal("!" + spec.Chat + " " + spec.Usage, CS2LineupFinder.Plugin.Commands.CommandUsage.UsageFor(spec.Console));
        }
    }
}
