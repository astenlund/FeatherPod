using FeatherPod.Models;
using FeatherPod.Services;
using FeatherPod.Middleware;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Configure port from environment variable (for Azure App Service)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Add services
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<EpisodeService>();
builder.Services.AddSingleton<RssFeedGenerator>();

var app = builder.Build();

// Add API key authentication middleware
app.UseMiddleware<ApiKeyAuthMiddleware>();

// Enable static file serving (for podcast icon, etc.)
app.UseStaticFiles();

// Initialize blob storage and episode service
var blobStorage = app.Services.GetRequiredService<IBlobStorageService>();
await blobStorage.InitializeAsync();

var episodeService = app.Services.GetRequiredService<EpisodeService>();
await episodeService.InitializeAsync();

// RSS Feed endpoint
app.MapGet("/feed.xml", async (EpisodeService service, RssFeedGenerator feedGenerator) =>
{
    var episodes = await service.GetAllEpisodesAsync();
    var feed = feedGenerator.GenerateFeed(episodes);
    return Results.Content(feed, "application/xml");
})
.WithName("GetPodcastFeed")
.Produces(200, contentType: "application/xml");

// Serve audio files from blob storage
app.MapGet("/audio/{filename}", async (string filename, IBlobStorageService blobStorage) =>
{
    if (!await blobStorage.AudioExistsAsync(filename))
        return Results.NotFound();

    var extension = Path.GetExtension(filename).ToLowerInvariant();
    var mimeType = extension switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        _ => "audio/mpeg"
    };

    var stream = await blobStorage.DownloadAudioAsync(filename);
    return Results.Stream(stream, mimeType, enableRangeProcessing: true);
})
.WithName("GetAudioFile")
.Produces(200)
.Produces(404);

// List all episodes
app.MapGet("/api/episodes", async (EpisodeService service) =>
{
    var episodes = await service.GetAllEpisodesAsync();
    return Results.Ok(episodes);
})
.WithName("ListEpisodes")
.Produces<List<Episode>>();

// Add new episode via API
app.MapPost("/api/episodes", async (
    [FromForm] IFormFile file,
    [FromForm] string? title,
    [FromForm] string? description,
    [FromForm] string? publishedDate,
    EpisodeService service,
    IConfiguration _) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest("No file uploaded");
    }

    // Parse published date if provided
    DateTime? parsedPublishedDate = null;
    if (!string.IsNullOrEmpty(publishedDate))
    {
        if (DateTime.TryParse(publishedDate, out var parsed))
        {
            parsedPublishedDate = parsed.ToUniversalTime();
        }
        else
        {
            return Results.BadRequest("Invalid publishedDate format. Use ISO 8601 format (e.g., 2024-01-15T10:30:00Z)");
        }
    }

    var tempPath = Path.Combine(Path.GetTempPath(), file.FileName);

    try
    {
        // Save uploaded file to temp location
        await using (var stream = File.Create(tempPath))
        {
            await file.CopyToAsync(stream);
        }

        // Add episode
        var episode = await service.AddEpisodeAsync(tempPath, title, description, parsedPublishedDate);

        return Results.Created($"/api/episodes/{episode.Id}", episode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
    finally
    {
        // Clean up temp file
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
})
.WithName("AddEpisode")
.Produces<Episode>(201)
.Produces(400)
.DisableAntiforgery();

// Delete episode
app.MapDelete("/api/episodes/{id}", async (string id, EpisodeService service) =>
{
    var deleted = await service.DeleteEpisodeAsync(id);

    return !deleted
        ? Results.NotFound()
        : Results.NoContent();
})
.WithName("DeleteEpisode")
.Produces(204)
.Produces(404);

app.Run();

// Make Program accessible to integration tests
public partial class Program { }
