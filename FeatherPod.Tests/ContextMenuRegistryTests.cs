using System.Runtime.Versioning;
using FeatherPod.Infrastructure;
using Microsoft.Win32;

namespace FeatherPod.Tests;

[Collection("Sequential")]
[SupportedOSPlatform("windows")]
public class ContextMenuRegistryTests : IDisposable
{
    private readonly string _registryPrefix;

    public ContextMenuRegistryTests()
    {
        _registryPrefix = $@"Software\FeatherPodTests_{Guid.NewGuid():N}";
    }

    [Fact]
    public void Install_CreatesCorrectRegistryKeys()
    {
        // Arrange
        var feedId = "test-podcast";
        var feedTitle = "Test Podcast";
        var bridgePath = @"C:\tools\featherpod-bridge.exe";
        var cliPath = @"C:\tools\featherpod.exe";
        var environment = "Prod";

        // Act
        ContextMenuRegistry.Install(feedId, feedTitle, bridgePath, cliPath, environment, _registryPrefix);

        // Assert
        using var shellKey = Registry.CurrentUser.OpenSubKey($@"{_registryPrefix}\.mp3\shell\FeatherPod.{feedId}");
        Assert.NotNull(shellKey);
        Assert.Equal("Push to Test Podcast", shellKey.GetValue(null));
        Assert.Equal(cliPath, shellKey.GetValue("Icon"));

        using var commandKey = Registry.CurrentUser.OpenSubKey($@"{_registryPrefix}\.mp3\shell\FeatherPod.{feedId}\command");
        Assert.NotNull(commandKey);
        var commandValue = commandKey.GetValue(null) as string;
        Assert.NotNull(commandValue);
        Assert.Contains(bridgePath, commandValue);
        Assert.Contains("--feed test-podcast", commandValue);
        Assert.Contains("--environment Prod", commandValue);
        Assert.Contains("--headless", commandValue);

        // Verify keys exist for other extensions too
        using var flacKey = Registry.CurrentUser.OpenSubKey($@"{_registryPrefix}\.flac\shell\FeatherPod.{feedId}");
        Assert.NotNull(flacKey);

        using var wavKey = Registry.CurrentUser.OpenSubKey($@"{_registryPrefix}\.wav\shell\FeatherPod.{feedId}");
        Assert.NotNull(wavKey);
    }

    [Fact]
    public void GetInstalled_ReturnsInstalledEntries()
    {
        // Arrange
        ContextMenuRegistry.Install("podcast-a", "Podcast A", @"C:\bridge.exe", @"C:\cli.exe", "Prod", _registryPrefix);
        ContextMenuRegistry.Install("podcast-b", "Podcast B", @"C:\bridge.exe", @"C:\cli.exe", "Test", _registryPrefix);

        // Act
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);

        // Assert
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.FeedId == "podcast-a" && e.FeedTitle == "Podcast A" && e.Environment == "Prod");
        Assert.Contains(entries, e => e.FeedId == "podcast-b" && e.FeedTitle == "Podcast B" && e.Environment == "Test");
    }

    [Fact]
    public void GetInstalled_ReturnsEmptyWhenNoneInstalled()
    {
        // Arrange (no installs)

        // Act
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);

        // Assert
        Assert.Empty(entries);
    }

    [Fact]
    public void GetInstalled_DeduplicatesAcrossExtensions()
    {
        // Arrange
        ContextMenuRegistry.Install("my-feed", "My Feed", @"C:\bridge.exe", @"C:\cli.exe", "Prod", _registryPrefix);

        // Act
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);

        // Assert - should return 1 entry even though it's registered under 9 extensions
        Assert.Single(entries);
        Assert.Equal("my-feed", entries[0].FeedId);
    }

    [Fact]
    public void Remove_RemovesSpecificFeed()
    {
        // Arrange
        ContextMenuRegistry.Install("keep-this", "Keep This", @"C:\bridge.exe", @"C:\cli.exe", "Prod", _registryPrefix);
        ContextMenuRegistry.Install("remove-this", "Remove This", @"C:\bridge.exe", @"C:\cli.exe", "Prod", _registryPrefix);

        // Act
        ContextMenuRegistry.Remove("remove-this", _registryPrefix);

        // Assert
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);
        Assert.Single(entries);
        Assert.Equal("keep-this", entries[0].FeedId);
    }

    [Fact]
    public void RemoveAll_RemovesAllEntries()
    {
        // Arrange
        ContextMenuRegistry.Install("feed-1", "Feed 1", @"C:\bridge.exe", @"C:\cli.exe", "Prod", _registryPrefix);
        ContextMenuRegistry.Install("feed-2", "Feed 2", @"C:\bridge.exe", @"C:\cli.exe", "Test", _registryPrefix);

        // Act
        ContextMenuRegistry.RemoveAll(_registryPrefix);

        // Assert
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);
        Assert.Empty(entries);
    }

    [Fact]
    public void Remove_NoOpWhenFeedNotInstalled()
    {
        // Arrange
        ContextMenuRegistry.Install("existing", "Existing", @"C:\bridge.exe", @"C:\cli.exe", "Prod", _registryPrefix);

        // Act - should not throw
        ContextMenuRegistry.Remove("nonexistent", _registryPrefix);

        // Assert
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);
        Assert.Single(entries);
        Assert.Equal("existing", entries[0].FeedId);
    }

    [Fact]
    public void Install_ParsesBridgePathFromCommand()
    {
        // Arrange
        var bridgePath = @"C:\Program Files\FeatherPod\featherpod-bridge.exe";
        ContextMenuRegistry.Install("path-test", "Path Test", bridgePath, @"C:\cli.exe", "Prod", _registryPrefix);

        // Act
        var entries = ContextMenuRegistry.GetInstalled(_registryPrefix);

        // Assert
        Assert.Single(entries);
        Assert.Equal(bridgePath, entries[0].BridgePath);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registryPrefix, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
