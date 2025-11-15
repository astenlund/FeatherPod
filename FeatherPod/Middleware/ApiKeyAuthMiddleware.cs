namespace FeatherPod.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _apiKey;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _apiKey = configuration["ApiKey"];
        _logger = logger;

        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("API key not configured. Management endpoints will be unprotected!");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // Check if this is a protected endpoint
        var isManagementEndpoint = IsProtectedEndpoint(path, method);

        if (isManagementEndpoint && !string.IsNullOrEmpty(_apiKey))
        {
            // Check for API key in header
            if (!context.Request.Headers.TryGetValue("X-API-Key", out var providedKey) ||
                providedKey != _apiKey)
            {
                _logger.LogWarning("Unauthorized API access attempt from {IP} to {Method} {Path}",
                    context.Connection.RemoteIpAddress, method, path);

                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Valid API key required." });
                return;
            }

            _logger.LogDebug("API key authenticated for {Method} {Path}", method, path);
        }

        await _next(context);
    }

    private static bool IsProtectedEndpoint(string path, string method)
    {
        // Feed management endpoints (POST, PUT, DELETE)
        if (path.StartsWith("/api/feeds"))
        {
            return method is "POST" or "PUT" or "DELETE";
        }

        // Episode management endpoints within feeds (POST, DELETE)
        if (path.Contains("/api/episodes"))
        {
            return method is "POST" or "DELETE";
        }

        return false;
    }
}
