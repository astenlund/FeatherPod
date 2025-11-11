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
        // Check if this is a management endpoint (POST or DELETE to /api/episodes)
        var isManagementEndpoint = context.Request.Path.StartsWithSegments("/api/episodes") &&
                                   (context.Request.Method == "POST" || context.Request.Method == "DELETE");

        if (isManagementEndpoint && !string.IsNullOrEmpty(_apiKey))
        {
            // Check for API key in header
            if (!context.Request.Headers.TryGetValue("X-API-Key", out var providedKey) ||
                providedKey != _apiKey)
            {
                _logger.LogWarning("Unauthorized API access attempt from {IP} to {Path}",
                    context.Connection.RemoteIpAddress, context.Request.Path);

                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Valid API key required." });
                return;
            }

            _logger.LogDebug("API key authenticated for {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        await _next(context);
    }
}
