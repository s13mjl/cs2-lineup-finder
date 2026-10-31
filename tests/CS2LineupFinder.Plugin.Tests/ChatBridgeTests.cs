using System;
using CS2LineupFinder.Plugin.Commands;
using Xunit;

namespace CS2LineupFinder.Plugin.Tests;

/// <summary>
/// The chat bridge forwards aliases over the player_chat game event while the
/// upstream chat trigger hook is broken. These tests pin the decision of which
/// messages cross the bridge and how they are rewritten.
/// </summary>
public sealed class ChatBridgeTests
{
    [Theory]
    [InlineData("!lf_zone circle 5", "lf_zone")]
    [InlineData("/lf_start", "lf_start")]
    [InlineData(".lf_find", "lf_find")]
    [InlineData("  !lf_menu  ", "lf_menu")]
    public void AliasMessages_ResolveToPluginCommands(string message, string expected)
    {
        Assert.Equal(expected, CommandUsage.CommandNameFromChat(message));
        Assert.True(CommandSpec.IsChatAlias(expected));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("!rtv")]
    [InlineData("!")]
    [InlineData("")]
    public void ForeignMessages_AreNotPluginAliases(string message)
    {
        var name = message.Length == 0 ? string.Empty : CommandUsage.CommandNameFromChat(message);
        Assert.False(CommandSpec.IsChatAlias(name));
    }

    [Fact]
    public void EveryRegisteredCommand_HasACoveredChatAlias()
    {
        foreach (var spec in CommandSpec.All)
        {
            Assert.True(CommandSpec.IsChatAlias(spec.Chat));
        }
    }
}
