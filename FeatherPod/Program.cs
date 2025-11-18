using FeatherPod.Models;
using FeatherPod.Services;
using FeatherPod.Middleware;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Configure port from environment variable (for Azure App Service)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Configure Kestrel for large file uploads
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB max file size
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

// Configure form options for large file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500 * 1024 * 1024; // 500 MB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Add services
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<EpisodeService>();
builder.Services.AddSingleton<IUserService, UserService>();

// Add background service for periodic blob storage sync
builder.Services.AddHostedService<BlobSyncBackgroundService>();

var app = builder.Build();

// Add API key authentication middleware
app.UseMiddleware<ApiKeyAuthMiddleware>();

// Initialize blob storage and episode service
await app.Services.GetRequiredService<IBlobStorageService>().InitializeAsync();
await app.Services.GetRequiredService<EpisodeService>().InitializeAsync();
await app.Services.GetRequiredService<IUserService>().LoadUsersAsync();

// Get base URL from configuration
var baseUrl = app.Configuration.GetSection("Podcast")["BaseUrl"]
    ?? throw new InvalidOperationException("Podcast.BaseUrl must be configured in appsettings.json");

// ============================================================================
// VERSION ENDPOINT
// ============================================================================

app.MapGet("/api/version", () =>
{
    var assembly = Assembly.GetEntryAssembly();
    var versionAttribute = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
    var version = versionAttribute?.InformationalVersion ?? "unknown";

    // Get build date from assembly file modification time
    var assemblyLocation = assembly?.Location;
    var buildDate = assemblyLocation != null && File.Exists(assemblyLocation)
        ? File.GetLastWriteTimeUtc(assemblyLocation).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        : DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    var versionInfo = new VersionInfo
    {
        Version = version,
        BuildDate = buildDate,
        Environment = app.Environment.EnvironmentName,
        TargetFramework = assembly?.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName
    };

    return Results.Ok(versionInfo);
})
.WithName("GetVersion")
.Produces<VersionInfo>();

// ============================================================================
// FEED MANAGEMENT ENDPOINTS
// ============================================================================

// List all feeds
app.MapGet("/api/feeds", async (EpisodeService service) =>
{
    var feeds = await service.GetFeedsAsync();
    return Results.Ok(feeds);
})
.WithName("ListFeeds")
.Produces<List<FeedConfig>>();

// Get specific feed
app.MapGet("/api/feeds/{feedId}", async (string feedId, EpisodeService service) =>
{
    var feed = await service.GetFeedAsync(feedId);
    return feed != null ? Results.Ok(feed) : Results.NotFound(new { error = $"Feed '{feedId}' not found" });
})
.WithName("GetFeed")
.Produces<FeedConfig>()
.Produces(404);

// Create new feed (requires API key)
app.MapPost("/api/feeds", async ([FromBody] FeedConfig feedConfig, EpisodeService service) =>
{
    try
    {
        var created = await service.CreateFeedAsync(feedConfig);
        return Results.Created($"/api/feeds/{created.Id}", created);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("CreateFeed")
.Produces<FeedConfig>(201)
.Produces(400);

// Update feed metadata (requires API key)
app.MapPut("/api/feeds/{feedId}", async (string feedId, [FromBody] FeedConfig feedConfig, EpisodeService service) =>
{
    try
    {
        var updated = await service.UpdateFeedAsync(feedId, feedConfig);
        return Results.Ok(updated);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
})
.WithName("UpdateFeed")
.Produces<FeedConfig>()
.Produces(404);

// Rename feed (requires API key)
app.MapPost("/api/feeds/{feedId}/rename", async (string feedId, [FromQuery] string newId, EpisodeService service) =>
{
    try
    {
        await service.RenameFeedAsync(feedId, newId);
        return Results.Ok(new { message = $"Feed renamed from '{feedId}' to '{newId}'" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("RenameFeed")
.Produces(200)
.Produces(400);

// Delete feed and all episodes (requires API key)
app.MapDelete("/api/feeds/{feedId}", async (string feedId, EpisodeService service) =>
{
    try
    {
        await service.DeleteFeedAsync(feedId);
        return Results.Ok(new { message = $"Feed '{feedId}' deleted" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
})
.WithName("DeleteFeed")
.Produces(200)
.Produces(404);

// ============================================================================
// EPISODE MANAGEMENT ENDPOINTS (with /api prefix)
// ============================================================================

// List episodes for a feed
app.MapGet("/api/feeds/{feedId}/episodes", async (string feedId, EpisodeService service) =>
{
    var feed = await service.GetFeedAsync(feedId);
    if (feed == null)
    {
        return Results.NotFound(new { error = $"Feed '{feedId}' not found" });
    }

    var episodes = await service.GetAllEpisodesAsync(feedId);

    // Populate URL for each episode
    var episodesWithUrls = episodes
        .Select(e => e with { Url = e.GetAudioUrl(baseUrl) })
        .ToList();

    return Results.Ok(episodesWithUrls);
})
.WithName("ListEpisodesApi")
.Produces<List<Episode>>()
.Produces(404);

// Upload episode to feed (requires API key)
app.MapPost("/api/feeds/{feedId}/episodes", async (
    string feedId,
    [FromForm] IFormFile? file,
    [FromForm] string? title,
    [FromForm] string? description,
    [FromForm] DateTime? publishedDate,
    EpisodeService service) =>
{
    var feed = await service.GetFeedAsync(feedId);
    if (feed == null)
    {
        return Results.NotFound(new { error = $"Feed '{feedId}' not found" });
    }

    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "No file uploaded" });
    }

    // Save uploaded file to temp location with original filename
    var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod", Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    var tempPath = Path.Combine(tempDir, file.FileName);

    await using (var stream = File.Create(tempPath))
    {
        await file.CopyToAsync(stream);
    }

    try
    {
        var episode = await service.AddEpisodeAsync(
            feedId,
            tempPath,
            title,
            description,
            publishedDate);

        var episodeWithUrl = episode with { Url = episode.GetAudioUrl(baseUrl) };

        return Results.Created($"/api/feeds/{feedId}/episodes/{episode.Id}", episodeWithUrl);
    }
    finally
    {
        // Clean up temp directory
        if (Directory.Exists(tempDir))
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
})
.WithName("UploadEpisodeApi")
.Produces<Episode>(201)
.Produces(400)
.Produces(404)
.DisableAntiforgery();

// Delete episode from feed (requires API key)
app.MapDelete("/api/feeds/{feedId}/episodes/{id}", async (string feedId, string id, EpisodeService service) =>
{
    var feed = await service.GetFeedAsync(feedId);
    if (feed == null)
    {
        return Results.NotFound(new { error = $"Feed '{feedId}' not found" });
    }

    var deleted = await service.DeleteEpisodeAsync(feedId, id);
    return deleted
        ? Results.Ok(new { message = $"Episode '{id}' deleted from feed '{feedId}'" })
        : Results.NotFound(new { error = $"Episode '{id}' not found in feed '{feedId}'" });
})
.WithName("DeleteEpisodeApi")
.Produces(200)
.Produces(404);

// Move episode between feeds (requires API key)
app.MapPost("/api/feeds/{feedId}/episodes/{id}/move", async (
    string feedId,
    string id,
    [FromBody] JsonElement body,
    EpisodeService service) =>
{
    if (!body.TryGetProperty("targetFeedId", out var targetFeedIdElement))
    {
        return Results.BadRequest(new { error = "targetFeedId is required in request body" });
    }

    var targetFeedId = targetFeedIdElement.GetString();
    if (string.IsNullOrEmpty(targetFeedId))
    {
        return Results.BadRequest(new { error = "targetFeedId cannot be empty" });
    }

    try
    {
        var movedEpisode = await service.MoveEpisodeAsync(id, feedId, targetFeedId);
        var episodeWithUrl = movedEpisode with { Url = movedEpisode.GetAudioUrl(baseUrl) };

        return Results.Ok(new
        {
            message = $"Episode '{id}' moved from '{feedId}' to '{targetFeedId}'",
            episode = episodeWithUrl
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("MoveEpisodeApi")
.Produces(200)
.Produces(400);

// Copy episode between feeds (requires API key)
app.MapPost("/api/feeds/{feedId}/episodes/{id}/copy", async (
    string feedId,
    string id,
    [FromBody] JsonElement body,
    EpisodeService service) =>
{
    if (!body.TryGetProperty("targetFeedId", out var targetFeedIdElement))
    {
        return Results.BadRequest(new { error = "targetFeedId is required in request body" });
    }

    var targetFeedId = targetFeedIdElement.GetString();
    if (string.IsNullOrEmpty(targetFeedId))
    {
        return Results.BadRequest(new { error = "targetFeedId cannot be empty" });
    }

    try
    {
        var copiedEpisode = await service.CopyEpisodeAsync(id, feedId, targetFeedId);
        var episodeWithUrl = copiedEpisode with { Url = copiedEpisode.GetAudioUrl(baseUrl) };

        return Results.Ok(new
        {
            message = $"Episode '{id}' copied from '{feedId}' to '{targetFeedId}'",
            episode = episodeWithUrl
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("CopyEpisodeApi")
.Produces(200)
.Produces(400);

// ============================================================================
// ICON MANAGEMENT ENDPOINTS (with /api prefix)
// ============================================================================

// Upload icon for specific feed (requires API key)
app.MapPost("/api/feeds/{feedId}/icon", async (
    string feedId,
    [FromForm] IFormFile? file,
    EpisodeService episodeService,
    IBlobStorageService blobService,
    ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("Icon upload request for feed '{FeedId}'", feedId);

        var feed = await episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            logger.LogWarning("Feed '{FeedId}' not found for icon upload", feedId);
            return Results.NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        if (file == null || file.Length == 0)
        {
            logger.LogWarning("No file uploaded for feed '{FeedId}'", feedId);
            return Results.BadRequest(new { error = "No file uploaded" });
        }

        logger.LogInformation("Uploading icon for feed '{FeedId}', size: {Size} bytes, type: {ContentType}",
            feedId, file.Length, file.ContentType);

        // Validate file type
        var allowedTypes = new[] { "image/png", "image/jpeg", "image/jpg" };
        if (!allowedTypes.Contains(file.ContentType.ToLower()))
        {
            logger.LogWarning("Invalid file type '{ContentType}' for feed '{FeedId}'", file.ContentType, feedId);
            return Results.BadRequest(new { error = "Only PNG and JPEG images are allowed" });
        }

        // Save uploaded file to temp location
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, "icon.png");

        await using (var stream = File.Create(tempPath))
        {
            await file.CopyToAsync(stream);
        }

        logger.LogDebug("Saved temporary icon to {TempPath}", tempPath);

        try
        {
            await blobService.UploadIconAsync(feedId, tempPath);
            logger.LogInformation("Successfully uploaded icon for feed '{FeedId}'", feedId);
            return Results.Ok(new { message = $"Icon uploaded for feed '{feedId}'", iconUrl = $"{baseUrl}/{feedId}/icon.png" });
        }
        finally
        {
            // Clean up temp file
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error uploading icon for feed '{FeedId}'", feedId);
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("UploadIconApi")
.DisableAntiforgery()
.Accepts<IFormFile>("multipart/form-data")
.Produces(200)
.Produces(400)
.Produces(401)
.Produces(404);

// Delete icon for specific feed (requires API key)
app.MapDelete("/api/feeds/{feedId}/icon", async (
    string feedId,
    EpisodeService episodeService,
    IBlobStorageService blobService,
    ILogger<Program> logger) =>
{
    var feed = await episodeService.GetFeedAsync(feedId);
    if (feed == null)
    {
        logger.LogWarning("Feed '{FeedId}' not found for icon deletion", feedId);
        return Results.NotFound(new { error = $"Feed '{feedId}' not found" });
    }

    await blobService.DeleteIconAsync(feedId);
    logger.LogInformation("Deleted icon for feed '{FeedId}'", feedId);

    return Results.NoContent();
})
.WithName("DeleteIconApi")
.Produces(204)
.Produces(401)
.Produces(404);

// ============================================================================
// USER MANAGEMENT ENDPOINTS (Admin only)
// ============================================================================

// List all users (Admin only)
app.MapGet("/api/users", async (IUserService userService) =>
{
    var users = await userService.GetAllUsersAsync();

    // Don't expose API key hashes
    var safeUsers = users.Select(u => new
    {
        u.Id,
        u.Name,
        u.Email,
        u.Role,
        u.OwnedFeeds,
        u.CreatedAt,
        u.LastActive,
        u.IsActive
    });

    return Results.Ok(safeUsers);
})
.WithName("ListUsers")
.Produces(200)
.Produces(401)
.Produces(403);

// Get user by ID (Admin only)
app.MapGet("/api/users/{userId}", async (string userId, IUserService userService) =>
{
    var user = await userService.GetUserByIdAsync(userId);
    if (user == null)
    {
        return Results.NotFound(new { error = $"User '{userId}' not found" });
    }

    // Don't expose API key hash
    var safeUser = new
    {
        user.Id,
        user.Name,
        user.Email,
        user.Role,
        user.OwnedFeeds,
        user.CreatedAt,
        user.LastActive,
        user.IsActive
    };

    return Results.Ok(safeUser);
})
.WithName("GetUser")
.Produces(200)
.Produces(401)
.Produces(403)
.Produces(404);

// Create user (Admin only) - returns API key ONCE
app.MapPost("/api/users", async ([FromBody] JsonElement body, IUserService userService, ILogger<Program> logger) =>
{
    if (!body.TryGetProperty("id", out var idElement) || string.IsNullOrWhiteSpace(idElement.GetString()))
    {
        return Results.BadRequest(new { error = "User ID is required" });
    }

    if (!body.TryGetProperty("name", out var nameElement) || string.IsNullOrWhiteSpace(nameElement.GetString()))
    {
        return Results.BadRequest(new { error = "Name is required" });
    }

    if (!body.TryGetProperty("email", out var emailElement) || string.IsNullOrWhiteSpace(emailElement.GetString()))
    {
        return Results.BadRequest(new { error = "Email is required" });
    }

    if (!body.TryGetProperty("role", out var roleElement) || !Enum.TryParse<UserRole>(roleElement.GetString(), true, out var role))
    {
        return Results.BadRequest(new { error = "Valid role is required (Admin or FeedOwner)" });
    }

    var ownedFeeds = new List<string>();
    if (body.TryGetProperty("ownedFeeds", out var ownedFeedsElement) && ownedFeedsElement.ValueKind == JsonValueKind.Array)
    {
        ownedFeeds = ownedFeedsElement.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    var user = new User
    {
        Id = idElement.GetString()!,
        Name = nameElement.GetString()!,
        Email = emailElement.GetString()!,
        Role = role,
        OwnedFeeds = ownedFeeds,
        ApiKeyHash = "" // Will be set by UserService
    };

    try
    {
        var apiKey = await userService.CreateUserAsync(user);
        logger.LogInformation("Created user '{UserId}' with role '{Role}'", user.Id, user.Role);

        return Results.Created($"/api/users/{user.Id}", new
        {
            message = $"User '{user.Id}' created successfully",
            user = new
            {
                user.Id,
                user.Name,
                user.Email,
                user.Role,
                user.OwnedFeeds
            },
            apiKey,
            warning = "This API key will only be shown once. Save it securely!"
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("CreateUser")
.Produces(201)
.Produces(400)
.Produces(401)
.Produces(403);

// Delete user (Admin only - soft delete)
app.MapDelete("/api/users/{userId}", async (string userId, IUserService userService, ILogger<Program> logger) =>
{
    var deleted = await userService.DeleteUserAsync(userId);
    if (!deleted)
    {
        return Results.NotFound(new { error = $"User '{userId}' not found" });
    }

    logger.LogInformation("Deleted user '{UserId}'", userId);
    return Results.Ok(new { message = $"User '{userId}' deleted" });
})
.WithName("DeleteUser")
.Produces(200)
.Produces(401)
.Produces(403)
.Produces(404);

// Regenerate user API key (Admin or self)
app.MapPost("/api/users/{userId}/key/regenerate", async (string userId, IUserService userService, ILogger<Program> logger) =>
{
    var newApiKey = await userService.RegenerateApiKeyAsync(userId);
    if (newApiKey == null)
    {
        return Results.NotFound(new { error = $"User '{userId}' not found" });
    }

    logger.LogInformation("Regenerated API key for user '{UserId}'", userId);

    return Results.Ok(new
    {
        message = $"API key regenerated for user '{userId}'",
        apiKey = newApiKey,
        warning = "This API key will only be shown once. Save it securely!"
    });
})
.WithName("RegenerateApiKey")
.Produces(200)
.Produces(401)
.Produces(403)
.Produces(404);

// Grant feed ownership (Admin only)
app.MapPost("/api/users/{userId}/feeds", async (string userId, [FromBody] JsonElement body, IUserService userService, ILogger<Program> logger) =>
{
    if (!body.TryGetProperty("feedId", out var feedIdElement) || string.IsNullOrWhiteSpace(feedIdElement.GetString()))
    {
        return Results.BadRequest(new { error = "feedId is required in request body" });
    }

    var feedId = feedIdElement.GetString()!;
    var granted = await userService.GrantFeedOwnershipAsync(userId, feedId);

    if (!granted)
    {
        return Results.BadRequest(new { error = $"Could not grant feed ownership. User '{userId}' not found or not a FeedOwner." });
    }

    logger.LogInformation("Granted feed '{FeedId}' ownership to user '{UserId}'", feedId, userId);
    return Results.Ok(new { message = $"Feed '{feedId}' ownership granted to user '{userId}'" });
})
.WithName("GrantFeedOwnership")
.Produces(200)
.Produces(400)
.Produces(401)
.Produces(403);

// Revoke feed ownership (Admin only)
app.MapDelete("/api/users/{userId}/feeds/{feedId}", async (string userId, string feedId, IUserService userService, ILogger<Program> logger) =>
{
    var revoked = await userService.RevokeFeedOwnershipAsync(userId, feedId);

    if (!revoked)
    {
        return Results.BadRequest(new { error = $"Could not revoke feed ownership. User '{userId}' not found or not a FeedOwner." });
    }

    logger.LogInformation("Revoked feed '{FeedId}' ownership from user '{UserId}'", feedId, userId);
    return Results.Ok(new { message = $"Feed '{feedId}' ownership revoked from user '{userId}'" });
})
.WithName("RevokeFeedOwnership")
.Produces(200)
.Produces(400)
.Produces(401)
.Produces(403);

// ============================================================================
// FEED-SPECIFIC ENDPOINTS
// ============================================================================

// RSS feed for specific feed
app.MapGet("/{feedId}/feed.xml", async (string feedId, EpisodeService service) =>
{
    var feed = await service.GetFeedAsync(feedId);
    if (feed == null)
    {
        return Results.NotFound($"Feed '{feedId}' not found");
    }

    var episodes = await service.GetAllEpisodesAsync(feedId);
    var xml = RssFeedGenerator.GenerateFeed(feed, baseUrl, episodes);

    return Results.Content(xml, "application/xml");
})
.WithName("GetRssFeed")
.Produces(200, contentType: "application/xml")
.Produces(404);

// Icon for specific feed (streams from blob storage)
app.MapGet("/{feedId}/icon.png", async (string feedId, IBlobStorageService service) =>
{
    var exists = await service.IconExistsAsync(feedId);
    if (!exists)
    {
        return Results.NotFound($"Icon for feed '{feedId}' not found");
    }

    var stream = await service.DownloadIconAsync(feedId);
    return Results.File(stream, "image/png");
})
.WithName("GetIcon")
.Produces(200, contentType: "image/png")
.Produces(404);

// Audio file streaming with range support
app.MapGet("/{feedId}/audio/{filename}", async (string feedId, string filename, IBlobStorageService service, HttpContext context) =>
{
    var exists = await service.AudioExistsAsync(feedId, filename);
    if (!exists)
    {
        return Results.NotFound($"Audio file '{filename}' not found in feed '{feedId}'");
    }

    var stream = await service.DownloadAudioAsync(feedId, filename);
    var fileSize = await service.GetAudioFileSizeAsync(feedId, filename);

    // Support range requests for audio streaming
    var range = context.Request.Headers.Range.ToString();
    if (!string.IsNullOrEmpty(range))
    {
        context.Response.StatusCode = 206; // Partial Content
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ContentRange = $"bytes 0-{fileSize - 1}/{fileSize}";
    }

    context.Response.ContentType = GetMimeType(filename);
    context.Response.ContentLength = fileSize;

    await stream.CopyToAsync(context.Response.Body);
    return Results.Empty;
})
.WithName("GetAudio")
.Produces(200)
.Produces(206)
.Produces(404);

// ============================================================================
// HELPER METHODS
// ============================================================================

static string GetMimeType(string fileName)
{
    var extension = Path.GetExtension(fileName).ToLowerInvariant();

    return extension switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        _ => "audio/mpeg"
    };
}

app.Run();
