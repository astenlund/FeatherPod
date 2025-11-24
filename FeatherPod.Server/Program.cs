using System.Text.Json.Serialization;
using FeatherPod.Server.Middleware;
using FeatherPod.Server.Services;
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

    context.Response.ContentType = AudioHelper.GetMimeType(filename);
    context.Response.ContentLength = fileSize;

    await stream.CopyToAsync(context.Response.Body);
    return Results.Empty;
})
.WithName("GetAudio")
.Produces(200)
.Produces(206)
.Produces(404);

app.Run();
