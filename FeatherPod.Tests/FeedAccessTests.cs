using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

public class FeedAccessTests
{
    [Fact]
    public void Admin_CanAccessAnyFeed()
    {
        // Arrange
        var admin = new CurrentUserInfo("admin", "Admin", []);

        // Act & Assert
        Assert.True(admin.CanAccessFeed("any-feed"));
        Assert.True(admin.CanAccessFeed("another-feed"));
    }

    [Fact]
    public void FeedOwner_CanAccessOwnedFeed()
    {
        // Arrange
        var owner = new CurrentUserInfo("user1", "FeedOwner", ["my-podcast", "other-feed"]);

        // Act & Assert
        Assert.True(owner.CanAccessFeed("my-podcast"));
        Assert.True(owner.CanAccessFeed("other-feed"));
    }

    [Fact]
    public void FeedOwner_CannotAccessUnownedFeed()
    {
        // Arrange
        var owner = new CurrentUserInfo("user1", "FeedOwner", ["my-podcast"]);

        // Act & Assert
        Assert.False(owner.CanAccessFeed("not-my-feed"));
    }

    [Fact]
    public void FeedOwner_WithNoFeeds_CannotAccessAnyFeed()
    {
        // Arrange
        var owner = new CurrentUserInfo("user1", "FeedOwner", []);

        // Act & Assert
        Assert.False(owner.CanAccessFeed("any-feed"));
    }

    [Fact]
    public void FeedOwner_FeedIdIsCaseSensitive()
    {
        // Arrange
        var owner = new CurrentUserInfo("user1", "FeedOwner", ["my-podcast"]);

        // Act & Assert
        Assert.True(owner.CanAccessFeed("my-podcast"));
        Assert.False(owner.CanAccessFeed("My-Podcast"));
    }
}
