using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;

using static System.StringSplitOptions;
using static FeatherPod.Shared.Models.UserRole;

namespace FeatherPod.Server.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _legacyApiKey;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _legacyApiKey = configuration["ApiKey"];
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IUserService userService)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // Check if this is a protected endpoint
        if (!IsProtectedEndpoint(path, method))
        {
            // Public endpoint - proceed without authentication
            await _next(context);

            return;
        }

        // Protected endpoint - require API key
        if (!context.Request.Headers.TryGetValue("X-API-Key", out var providedKey) || string.IsNullOrWhiteSpace(providedKey))
        {
            _logger.LogWarning("Unauthorized API access attempt (no API key) from {IP} to {Method} {Path}", context.Connection.RemoteIpAddress, method, path);

            context.Response.StatusCode = 401;

            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Valid API key required." });

            return;
        }

        // Look up user by API key
        var user = await userService.GetUserByApiKeyAsync(providedKey!);

        // If no user found, check legacy API key for migration support
        if (user == null && !string.IsNullOrEmpty(_legacyApiKey) && providedKey == _legacyApiKey)
        {
            _logger.LogWarning("Legacy API key used for {Method} {Path}. Please migrate to user-specific API keys.", method, path);

            // Create a virtual admin user for legacy key
            user = new()
            {
                Id = "legacy-admin",
                Name = "Legacy Admin",
                Email = "legacy@featherpod.local",
                Role = Admin,
                ApiKeyHash = "",
                OwnedFeeds = []
            };
        }

        if (user == null)
        {
            _logger.LogWarning("Unauthorized API access attempt (invalid API key) from {IP} to {Method} {Path}", context.Connection.RemoteIpAddress, method, path);

            context.Response.StatusCode = 401;

            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Invalid API key." });

            return;
        }

        // Store user in context for downstream use
        context.Items["User"] = user;

        // Update last active timestamp
        if (user.Id != "legacy-admin")
        {
            try
            {
                await userService.UpdateLastActiveAsync(user.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update last active timestamp for user '{UserId}'", user.Id);
            }
        }

        // Check permissions based on endpoint type
        var authorized = await CheckPermissionsAsync(user, path, userService);

        if (!authorized)
        {
            _logger.LogWarning("Forbidden API access attempt from user '{UserId}' ({Role}) to {Method} {Path}",
                user.Id, user.Role, method, path);

            context.Response.StatusCode = 403;

            await context.Response.WriteAsJsonAsync(new { error = "Forbidden. Insufficient permissions." });

            return;
        }

        _logger.LogDebug("User '{UserId}' ({Role}) authenticated for {Method} {Path}", user.Id, user.Role, method, path);

        await _next(context);
    }

    private static bool IsProtectedEndpoint(string path, string method)
    {
        // Public endpoints (no authentication required)
        if (method == "GET")
        {
            // Public read endpoints
            if (path.StartsWith("/api/version") ||
                path.StartsWith("/api/feeds") && !path.Contains("/episodes") && !path.Contains("/icon") ||
                path.StartsWith("/api/jobs/") ||
                path.EndsWith("/feed.xml") ||
                path.EndsWith("/icon.png") ||
                path.Contains("/audio/"))
            {
                return false;
            }
        }

        // Internal endpoints use their own X-Internal-Key authentication
        if (path.StartsWith("/api/internal/"))
        {
            return false;
        }

        // All other /api/* endpoints are protected by default
        if (path.StartsWith("/api/"))
        {
            return true;
        }

        // Old-style endpoints (to be removed after migration)
        return path.Contains("/api/episodes") || path.Contains("/api/icon");
    }

    private static async Task<bool> CheckPermissionsAsync(User user, string path, IUserService userService)
    {
        // Admin users have access to everything
        if (user.Role == Admin)
        {
            return true;
        }

        // Job status polling - allow any authenticated user
        // Note: Users can only get job IDs from their own upload responses
        if (path.StartsWith("/api/jobs/"))
        {
            return true;
        }

        // User management endpoints are admin-only, except /api/users/me and rotating own key
        if (path.StartsWith("/api/users"))
        {
            // Allow any authenticated user to access /api/users/me
            if (path.Equals("/api/users/me", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Allow users to rotate their own API key
            if (path.Equals($"/api/users/{user.Id}/key/regenerate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false; // FeedOwner cannot access other user management endpoints
        }

        // Feed-specific endpoints - check feed ownership
        if (path.StartsWith("/api/feeds/"))
        {
            var feedId = ExtractFeedId(path);
            if (feedId != null)
            {
                return await userService.ValidatePermissionAsync(user, feedId);
            }
        }

        // Old-style feed-specific endpoints (to be removed after migration)
        if (path.Contains("/api/episodes") || path.Contains("/api/icon"))
        {
            var feedId = ExtractFeedIdFromLegacyPath(path);
            if (feedId != null)
            {
                return await userService.ValidatePermissionAsync(user, feedId);
            }
        }

        // Default deny
        return false;
    }

    private static string? ExtractFeedId(string path)
    {
        // Extract feedId from paths like:
        // /api/feeds/{feedId}/episodes
        // /api/feeds/{feedId}/icon
        // /api/feeds/{feedId}/episodes/{id}/move

        if (!path.StartsWith("/api/feeds/"))
        {
            return null;
        }

        var segments = path.Split('/', RemoveEmptyEntries);

        // Format: api/feeds/{feedId}/...
        return segments.Length >= 3 ?
            segments[2] : // feedId is the 3rd segment (index 2)
            null;
    }

    private static string? ExtractFeedIdFromLegacyPath(string path)
    {
        // Extract feedId from legacy paths like:
        // /{feedId}/api/episodes
        // /{feedId}/api/icon

        var segments = path.Split('/', RemoveEmptyEntries);

        // Format: {feedId}/api/...
        return segments is [_, "api", ..] ?
            segments[0] : // feedId is the 1st segment (index 0)
            null;
    }
}
