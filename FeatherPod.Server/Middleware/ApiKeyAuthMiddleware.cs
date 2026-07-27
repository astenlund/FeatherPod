using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;

using static System.StringComparison;
using static System.StringSplitOptions;
using static FeatherPod.Shared.Models.UserRole;

namespace FeatherPod.Server.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
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
        try
        {
            await userService.UpdateLastActiveAsync(user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update last active timestamp for user '{UserId}'", user.Id);
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
        // Endpoint routing matches paths case-insensitively, so every comparison here must too --
        // a case-sensitive miss would classify a routable request as unprotected and skip auth entirely
        // Public endpoints (no authentication required)
        if (HttpMethods.IsGet(method))
        {
            // Public read endpoints
            if (path.StartsWith("/api/version", OrdinalIgnoreCase) ||
                path.StartsWith("/api/feeds", OrdinalIgnoreCase) && !path.Contains("/episodes", OrdinalIgnoreCase) && !path.Contains("/icon", OrdinalIgnoreCase) &&
                    !path.Contains("/check-integrity", OrdinalIgnoreCase) && !path.Contains("/jobs", OrdinalIgnoreCase) ||
                path.StartsWith("/api/jobs/", OrdinalIgnoreCase) ||
                path.EndsWith("/feed.xml", OrdinalIgnoreCase) ||
                path.EndsWith("/icon.png", OrdinalIgnoreCase) ||
                path.Contains("/audio/", OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Internal endpoints use their own X-Internal-Key authentication
        if (path.StartsWith("/api/internal/", OrdinalIgnoreCase))
        {
            return false;
        }

        // All other /api/* endpoints are protected by default
        if (path.StartsWith("/api/", OrdinalIgnoreCase))
        {
            return true;
        }

        // Old-style endpoints (to be removed after migration)
        return path.Contains("/api/episodes", OrdinalIgnoreCase) || path.Contains("/api/icon", OrdinalIgnoreCase);
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
        if (path.StartsWith("/api/jobs/", OrdinalIgnoreCase))
        {
            return true;
        }

        // YouTube cookie management - allow any authenticated user
        // Admin-only restrictions (POST, DELETE) are handled inside the controller
        if (path.StartsWith("/api/youtube/cookies", OrdinalIgnoreCase))
        {
            return true;
        }

        // User management endpoints are admin-only, except /api/users/me and rotating own key
        if (path.StartsWith("/api/users", OrdinalIgnoreCase))
        {
            // Allow any authenticated user to access /api/users/me
            if (path.Equals("/api/users/me", OrdinalIgnoreCase))
            {
                return true;
            }

            // Allow users to rotate their own API key
            if (path.Equals($"/api/users/{user.Id}/key/regenerate", OrdinalIgnoreCase))
            {
                return true;
            }

            return false; // FeedOwner cannot access other user management endpoints
        }

        // Feed-level endpoints that handle their own authorization
        if (path.Equals("/api/feeds/check-integrity", OrdinalIgnoreCase))
        {
            return true;
        }

        // Feed-specific endpoints - check feed ownership
        if (path.StartsWith("/api/feeds/", OrdinalIgnoreCase))
        {
            var feedId = ExtractFeedId(path);
            if (feedId != null)
            {
                return await userService.ValidatePermissionAsync(user, feedId);
            }
        }

        // Old-style feed-specific endpoints (to be removed after migration)
        if (path.Contains("/api/episodes", OrdinalIgnoreCase) || path.Contains("/api/icon", OrdinalIgnoreCase))
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

        if (!path.StartsWith("/api/feeds/", OrdinalIgnoreCase))
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

        // Format: {feedId}/api/... (the "api" literal matches case-insensitively like routing; the feedId keeps its case)
        return segments is [var feedId, var api, ..] && api.Equals("api", OrdinalIgnoreCase) ?
            feedId :
            null;
    }
}
