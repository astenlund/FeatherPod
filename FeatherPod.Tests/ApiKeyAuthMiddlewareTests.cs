using FeatherPod.Server.Middleware;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class ApiKeyAuthMiddlewareTests
{
    private const string ApiKey = "fp_user1_secret";

    [Fact]
    public async Task InvokeAsync_UppercaseApiPrefix_WithoutKey_Returns401()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("PUT", "/API/feeds/test-feed", userService);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_MixedCaseApiPrefix_WithoutKey_Returns401()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("DELETE", "/Api/Feeds/test-feed", userService);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_LowercaseApiPrefix_WithoutKey_Returns401()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("PUT", "/api/feeds/test-feed", userService);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_UppercaseLegacyEpisodesPath_WithoutKey_Returns401()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("POST", "/my-feed/API/episodes", userService);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_UppercaseEpisodesSegmentOnGet_WithoutKey_Returns401()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("GET", "/api/feeds/test-feed/EPISODES", userService);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_LowercaseGetMethod_VersionEndpoint_PassesThroughWithoutAuth()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("get", "/api/version", userService);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_UppercaseVersionEndpoint_PassesThroughWithoutAuth()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("GET", "/API/version", userService);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_UppercaseFeedListEndpoint_PassesThroughWithoutAuth()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("GET", "/API/feeds", userService);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_UppercaseInternalEndpoint_PassesThroughWithoutAuth()
    {
        // Arrange
        var userService = new StubUserService();

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("POST", "/API/internal/jobs/job1/normalization-complete", userService);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_FeedOwner_UppercasePathToOwnedFeed_IsAuthenticatedAndAuthorized()
    {
        // Arrange
        var owner = CreateFeedOwner();
        var userService = new StubUserService
        {
            UserForApiKey = owner,
            ValidatePermission = (_, feedId) => feedId == "my-feed",
        };

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("PUT", "/API/feeds/my-feed", userService, ApiKey);

        // Assert
        Assert.True(nextCalled);
        Assert.Same(owner, context.Items["User"]);
    }

    [Fact]
    public async Task InvokeAsync_FeedOwner_UppercasePathToUnownedFeed_Returns403()
    {
        // Arrange
        var userService = new StubUserService
        {
            UserForApiKey = CreateFeedOwner(),
            ValidatePermission = (_, _) => false,
        };

        // Act
        var (context, nextCalled) = await RunMiddlewareAsync("PUT", "/API/feeds/other-feed", userService, ApiKey);

        // Assert
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_FeedOwner_UppercasePath_PreservesFeedIdCaseForPermissionCheck()
    {
        // Arrange
        string? checkedFeedId = null;
        var userService = new StubUserService
        {
            UserForApiKey = CreateFeedOwner(),
            ValidatePermission = (_, feedId) =>
            {
                checkedFeedId = feedId;

                return true;
            },
        };

        // Act
        await RunMiddlewareAsync("PUT", "/API/feeds/My-Feed/episodes", userService, ApiKey);

        // Assert
        Assert.Equal("My-Feed", checkedFeedId);
    }

    [Fact]
    public async Task InvokeAsync_FeedOwner_UppercaseLegacyPath_ExtractsFeedIdWithCasePreserved()
    {
        // Arrange
        string? checkedFeedId = null;
        var userService = new StubUserService
        {
            UserForApiKey = CreateFeedOwner(),
            ValidatePermission = (_, feedId) =>
            {
                checkedFeedId = feedId;

                return true;
            },
        };

        // Act
        var (_, nextCalled) = await RunMiddlewareAsync("POST", "/My-Feed/API/episodes", userService, ApiKey);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal("My-Feed", checkedFeedId);
    }

    private static User CreateFeedOwner() => new()
    {
        Id = "user1",
        Name = "User One",
        ApiKeyHash = "hash",
        Role = UserRole.FeedOwner,
    };

    private static async Task<(HttpContext Context, bool NextCalled)> RunMiddlewareAsync(string method, string path, IUserService userService, string? apiKey = null)
    {
        var nextCalled = false;
        var middleware = new ApiKeyAuthMiddleware(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (apiKey != null)
        {
            context.Request.Headers["X-API-Key"] = apiKey;
        }

        await middleware.InvokeAsync(context, userService);

        return (context, nextCalled);
    }

    private sealed class StubUserService : IUserService
    {
        public User? UserForApiKey { get; init; }

        public Func<User, string, bool> ValidatePermission { get; init; } = (_, _) => false;

        public Task LoadUsersAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<User>> GetAllUsersAsync() => throw new NotSupportedException();

        public Task<User?> GetUserByIdAsync(string userId) => throw new NotSupportedException();

        public Task<User?> GetUserByApiKeyAsync(string apiKey) => Task.FromResult(UserForApiKey);

        public Task<string> CreateUserAsync(User user) => throw new NotSupportedException();

        public Task<bool> DeleteUserAsync(string userId) => throw new NotSupportedException();

        public Task<string?> RegenerateApiKeyAsync(string userId) => throw new NotSupportedException();

        public Task UpdateLastActiveAsync(string userId) => Task.CompletedTask;

        public Task<bool> GrantFeedOwnershipAsync(string userId, string feedId) => throw new NotSupportedException();

        public Task<bool> RevokeFeedOwnershipAsync(string userId, string feedId) => throw new NotSupportedException();

        public Task<bool> ValidatePermissionAsync(User user, string feedId) => Task.FromResult(ValidatePermission(user, feedId));
    }
}
