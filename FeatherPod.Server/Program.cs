using System.Text.Json.Serialization;
using FeatherPod.Server.Middleware;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared;

const long MaxUploadSizeBytes = 500 * 1024 * 1024; // 500 MB

var builder = WebApplication.CreateBuilder(args);

// Configure port from environment variable (for Azure App Service)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Configure Kestrel for large file uploads
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxUploadSizeBytes;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

// Configure form options for large file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadSizeBytes;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Add services
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<EpisodeService>();
builder.Services.AddSingleton<IUserService, UserService>();

// Add background service for periodic blob storage sync
builder.Services.AddHostedService<BlobSyncBackgroundService>();

// Add controllers
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();

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

// Map controllers (handles all /api/* endpoints)
app.MapControllers();

// ============================================================================
// HEALTH CHECK ENDPOINT
// ============================================================================

// Simple health check endpoint for Azure Monitor health probes
app.MapGet("/health", () => Results.Ok())
    .WithName("GetHealth")
    .Produces(200);

// ============================================================================
// FEED-SPECIFIC ENDPOINTS
// ============================================================================

// RSS feed for specific feed
app.MapGet("/{feedId}/feed.xml", async (string feedId, EpisodeService service) =>
{
    if (!InputValidation.IsValidFeedId(feedId))
    {
        return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
    }

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
app.MapGet("/{feedId}/icon.png", async (string feedId, IBlobStorageService service, HttpContext context) =>
{
    if (!InputValidation.IsValidFeedId(feedId))
    {
        return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
    }

    var exists = await service.IconExistsAsync(feedId);
    if (!exists)
    {
        return Results.NotFound($"Icon for feed '{feedId}' not found");
    }

    var stream = await service.DownloadIconAsync(feedId);
    context.Response.ContentType = "image/png";

    await using (stream)
    {
        await stream.CopyToAsync(context.Response.Body);
    }

    return Results.Empty;
})
.WithName("GetIcon")
.Produces(200, contentType: "image/png")
.Produces(404);

// Audio file streaming with range support
app.MapGet("/{feedId}/audio/{filename}", async (string feedId, string filename, IBlobStorageService service, HttpContext context) =>
{
    // Validate feedId and filename to prevent path traversal
    if (!InputValidation.IsValidFeedId(feedId))
    {
        return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
    }

    if (!InputValidation.IsValidFilename(filename))
    {
        return Results.BadRequest(new { error = InputValidation.GetFilenameValidationError(filename) });
    }

    var exists = await service.AudioExistsAsync(feedId, filename);
    if (!exists)
    {
        return Results.NotFound($"Audio file '{filename}' not found in feed '{feedId}'");
    }

    var fileSize = await service.GetAudioFileSizeAsync(feedId, filename);
    var rangeHeader = context.Request.Headers.Range.ToString();

    context.Response.Headers.AcceptRanges = "bytes";
    context.Response.ContentType = AudioHelper.GetMimeType(filename);

    // Parse and handle range requests for audio streaming (e.g., seeking in podcast players)
    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
    {
        var rangeValue = rangeHeader["bytes=".Length..];
        var rangeParts = rangeValue.Split('-');

        var rangeStart = long.TryParse(rangeParts[0], out var start) ? start : 0;
        var rangeEnd = rangeParts.Length > 1 && long.TryParse(rangeParts[1], out var end) ? end : fileSize - 1;

        // Clamp values to valid range
        rangeStart = Math.Max(0, Math.Min(rangeStart, fileSize - 1));
        rangeEnd = Math.Max(rangeStart, Math.Min(rangeEnd, fileSize - 1));

        var contentLength = rangeEnd - rangeStart + 1;

        context.Response.StatusCode = 206; // Partial Content
        context.Response.Headers.ContentRange = $"bytes {rangeStart}-{rangeEnd}/{fileSize}";
        context.Response.ContentLength = contentLength;

        // Download to temp file and stream the requested range
        var tempPath = await service.DownloadAudioToTempAsync(feedId, filename);
        try
        {
            await using var fileStream = File.OpenRead(tempPath);
            fileStream.Seek(rangeStart, SeekOrigin.Begin);

            var buffer = new byte[81920]; // 80KB buffer
            var remaining = contentLength;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, toRead));
                if (bytesRead == 0) break;

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead));
                remaining -= bytesRead;
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
        }
    }
    else
    {
        // Full file request
        context.Response.ContentLength = fileSize;
        var stream = await service.DownloadAudioAsync(feedId, filename);
        await using (stream)
        {
            await stream.CopyToAsync(context.Response.Body);
        }
    }

    return Results.Empty;
})
.WithName("GetAudio")
.Produces(200)
.Produces(206)
.Produces(404);

app.Run();
