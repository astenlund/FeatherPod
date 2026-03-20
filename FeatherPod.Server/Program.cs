using System.Text.Json.Serialization;
using FeatherPod.Server.Hubs;
using FeatherPod.Server.Middleware;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared;

const long MaxUploadSizeBytes = 500 * 1024 * 1024; // 500 MB

var builder = WebApplication.CreateBuilder(args);

// Configure port from environment variable (for Azure App Service)
// Use 0.0.0.0 to allow LAN access for mobile testing
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
builder.Services.AddSingleton<IJobService, JobService>();
builder.Services.AddSingleton<IJobProgressChannel, JobProgressChannel>();
builder.Services.AddSingleton<IFeedEventChannel, FeedEventChannel>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IconResizeService>();

// Add background service for periodic blob storage sync
builder.Services.AddHostedService<BlobSyncBackgroundService>();

// Add controllers
builder.Services
    .AddControllers()
    .AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()); });

var signalRBuilder = builder.Services.AddSignalR();
var signalRConnectionString = builder.Configuration["Azure:SignalR:ConnectionString"];
if (!string.IsNullOrEmpty(signalRConnectionString))
{
    signalRBuilder.AddAzureSignalR(signalRConnectionString);
}

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// Add API key authentication middleware
app.UseMiddleware<ApiKeyAuthMiddleware>();

// Initialize blob storage and episode service
await app.Services.GetRequiredService<IBlobStorageService>().InitializeAsync();
await app.Services.GetRequiredService<EpisodeService>().InitializeAsync();
await app.Services.GetRequiredService<IUserService>().LoadUsersAsync();
await app.Services.GetRequiredService<IJobService>().InitializeAsync();

// Get base URL from configuration
var baseUrl = app.Configuration.GetSection("Podcast")["BaseUrl"]
              ?? throw new InvalidOperationException("Podcast.BaseUrl must be configured in appsettings.json");

// Map controllers (handles all /api/* endpoints)
app.MapControllers();

// Map SignalR hub for real-time progress push from Function App
app.MapHub<ProgressHub>("/api/internal/signalrhub");

// ============================================================================
// HEALTH CHECK ENDPOINT
// ============================================================================

// Health check endpoint for Azure Monitor health probes
app.MapGet("/health", async (EpisodeService episodeService) =>
    {
        try
        {
            var feeds = await episodeService.GetFeedsAsync();
            return Results.Ok(new
            {
                Status = "Healthy",
                FeedCount = feeds.Count,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                Status = "Unhealthy",
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            }, statusCode: 503);
        }
    })
    .WithName("GetHealth")
    .Produces(200)
    .Produces(503);

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

        Stream stream;
        try
        {
            stream = await service.DownloadIconAsync(feedId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return Results.NotFound($"Icon for feed '{feedId}' not found");
        }

        context.Response.ContentType = "image/png";
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        await using (stream)
        {
            await stream.CopyToAsync(context.Response.Body);
        }

        return Results.Empty;
    })
    .WithName("GetIcon")
    .Produces(200, contentType: "image/png")
    .Produces(404);

// Resized icon for PWA manifest (192x192 or 512x512)
app.MapGet("/{feedId}/icon-{size:int}.png", async (string feedId, int size, IconResizeService iconResizeService, HttpContext context) =>
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        if (!IconResizeService.IsValidSize(size))
        {
            return Results.BadRequest(new { error = "Icon size must be 192 or 512" });
        }

        var bytes = await iconResizeService.GetResizedIconAsync(feedId, size);
        if (bytes == null)
        {
            return Results.NotFound($"Icon for feed '{feedId}' not found");
        }

        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.Bytes(bytes, "image/png");
    })
    .WithName("GetResizedIcon")
    .Produces(200, contentType: "image/png")
    .Produces(400)
    .Produces(404);

// Browser-based upload page for quick mobile uploads
app.MapGet("/{feedId}/push",
        async (string feedId, EpisodeService episodeService, IBlobStorageService blobStorageService, IWebHostEnvironment env, IConfiguration config, HttpContext context) =>
        {
            if (!InputValidation.IsValidFeedId(feedId))
            {
                return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
            }

            var feed = await episodeService.GetFeedAsync(feedId);
            if (feed == null)
            {
                return Results.NotFound($"Feed '{feedId}' not found");
            }

            var progressSmoothing = config.GetValue("PushPage:ProgressSmoothing", true);
            var userAgent = context.Request.Headers.UserAgent.ToString();
            var isAndroid = userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase);
            var iconETag = await blobStorageService.GetIconETagAsync(feedId);
            var pwaEnabled = isAndroid && iconETag != null;

            return Results.Content(GeneratePushPageHtml(feedId, feed.Title, env, progressSmoothing, pwaEnabled, iconETag), "text/html");
        })
    .WithName("GetPushPage")
    .Produces(200, contentType: "text/html")
    .Produces(400)
    .Produces(404);

// Fallback for share target POST when service worker is not active
app.MapPost("/{feedId}/push", (string feedId) => Results.Redirect($"/{feedId}/push", permanent: false, preserveMethod: false))
    .WithName("PostPushFallback")
    .Produces(302);

// PWA Web App Manifest for share target support (Android)
app.MapGet("/{feedId}/push/manifest.json", async (string feedId, EpisodeService episodeService, IBlobStorageService blobStorageService) =>
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var feed = await episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            return Results.NotFound($"Feed '{feedId}' not found");
        }

        var iconETag = await blobStorageService.GetIconETagAsync(feedId);
        var iconCacheBuster = IconCacheBuster(iconETag);
        var shortName = feed.Title.Length <= 12 ? feed.Title : feed.Title[..12];
        var manifest = new
        {
            name = $"Push to {feed.Title}",
            short_name = shortName,
            start_url = $"/{feedId}/push",
            scope = $"/{feedId}/push",
            display = "standalone",
            background_color = "#1a1a2e",
            theme_color = "#1a1a2e",
            icons = new[]
            {
                new { src = $"/{feedId}/icon-192.png{iconCacheBuster}", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = $"/{feedId}/icon-512.png{iconCacheBuster}", sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = $"/{feedId}/icon-192.png{iconCacheBuster}", sizes = "192x192", type = "image/png", purpose = "maskable" },
                new { src = $"/{feedId}/icon-512.png{iconCacheBuster}", sizes = "512x512", type = "image/png", purpose = "maskable" }
            },
            share_target = new
            {
                action = $"/{feedId}/push",
                method = "POST",
                enctype = "multipart/form-data",
                @params = new
                {
                    files = new[]
                    {
                        new
                        {
                            name = "audio",
                            accept = new[] { "audio/*" }
                        }
                    }
                }
            }
        };

        return Results.Json(manifest, contentType: "application/manifest+json");
    })
    .WithName("GetPushManifest")
    .Produces(200, contentType: "application/manifest+json")
    .Produces(400)
    .Produces(404);

// Service worker for PWA share target interception
app.MapGet("/{feedId}/push/sw.js", (string feedId, IWebHostEnvironment env, HttpContext context) =>
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        string js;
        if (env.IsDevelopment())
        {
            var pushDir = Path.Combine(env.ContentRootPath, "Pages", "Push");
            js = File.ReadAllText(Path.Combine(pushDir, "push-sw.js"));
        }
        else
        {
            var assembly = typeof(Program).Assembly;
            js = ReadResource(assembly, "FeatherPod.Server.Pages.Push.push-sw.js");
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["Service-Worker-Allowed"] = "/";

        return Results.Content(js, "application/javascript");
    })
    .WithName("GetPushServiceWorker")
    .Produces(200, contentType: "application/javascript")
    .Produces(400);

app.MapGet("/{feedId}/push/app.js", (string feedId, IWebHostEnvironment env, HttpContext context) =>
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        string js;
        if (env.IsDevelopment())
        {
            var pushDir = Path.Combine(env.ContentRootPath, "Pages", "Push");
            js = File.ReadAllText(Path.Combine(pushDir, "push.js"));
        }
        else
        {
            var assembly = typeof(Program).Assembly;
            js = ReadResource(assembly, "FeatherPod.Server.Pages.Push.push.js");
        }

        context.Response.Headers.CacheControl = "no-cache";

        return Results.Content(js, "application/javascript");
    })
    .WithName("GetPushAppJs")
    .Produces(200, contentType: "application/javascript")
    .Produces(400);

app.MapGet("/{feedId}/push/app.css", (string feedId, IWebHostEnvironment env, HttpContext context) =>
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        string css;
        if (env.IsDevelopment())
        {
            var pushDir = Path.Combine(env.ContentRootPath, "Pages", "Push");
            css = File.ReadAllText(Path.Combine(pushDir, "push.css"));
        }
        else
        {
            var assembly = typeof(Program).Assembly;
            css = ReadResource(assembly, "FeatherPod.Server.Pages.Push.push.css");
        }

        context.Response.Headers.CacheControl = "no-cache";

        return Results.Content(css, "text/css");
    })
    .WithName("GetPushAppCss")
    .Produces(200, contentType: "text/css")
    .Produces(400);

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
        // Implements RFC 7233: https://www.rfc-editor.org/rfc/rfc7233
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
        {
            var rangeValue = rangeHeader["bytes=".Length..];
            var rangeParts = rangeValue.Split('-');

            long rangeStart;
            long rangeEnd;

            // Handle suffix range: bytes=-N (last N bytes)
            if (string.IsNullOrEmpty(rangeParts[0]) && rangeParts.Length > 1 && long.TryParse(rangeParts[1], out var suffixLength))
            {
                if (suffixLength <= 0)
                {
                    context.Response.StatusCode = 416; // Range Not Satisfiable
                    context.Response.Headers.ContentRange = $"bytes */{fileSize}";
                    return Results.Empty;
                }

                rangeStart = Math.Max(0, fileSize - suffixLength);
                rangeEnd = fileSize - 1;
            }
            // Handle standard range: bytes=N-M or bytes=N-
            else if (long.TryParse(rangeParts[0], out var start))
            {
                rangeStart = start;
                rangeEnd = rangeParts.Length > 1 && long.TryParse(rangeParts[1], out var end) ? end : fileSize - 1;
            }
            else
            {
                // Malformed range header
                context.Response.StatusCode = 416; // Range Not Satisfiable
                context.Response.Headers.ContentRange = $"bytes */{fileSize}";
                return Results.Empty;
            }

            // Validate range is satisfiable
            if (rangeStart >= fileSize || rangeStart < 0 || rangeEnd < rangeStart)
            {
                context.Response.StatusCode = 416; // Range Not Satisfiable
                context.Response.Headers.ContentRange = $"bytes */{fileSize}";
                return Results.Empty;
            }

            // Clamp rangeEnd to file bounds (valid per RFC 7233)
            rangeEnd = Math.Min(rangeEnd, fileSize - 1);
            var contentLength = rangeEnd - rangeStart + 1;

            context.Response.StatusCode = 206; // Partial Content
            context.Response.Headers.ContentRange = $"bytes {rangeStart}-{rangeEnd}/{fileSize}";
            context.Response.ContentLength = contentLength;

            // Use native Azure range download - no temp file needed
            var stream = await service.DownloadAudioRangeAsync(feedId, filename, rangeStart, contentLength);
            await using (stream)
            {
                await stream.CopyToAsync(context.Response.Body);
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
    .Produces(404)
    .Produces(416);

app.Run();

// ============================================================================
// PUSH PAGE HTML GENERATION
// ============================================================================

static string GeneratePushPageHtml(string feedId, string feedTitle, IWebHostEnvironment env, bool progressSmoothing, bool pwaEnabled, string? iconETag)
{
    var escapedTitle = System.Net.WebUtility.HtmlEncode(feedTitle);
    var hasArtwork = iconETag != null;
    var iconCacheBuster = IconCacheBuster(iconETag);

    string html;

    if (env.IsDevelopment())
    {
        // In development, read from disk for hot-reload
        var pushDir = Path.Combine(env.ContentRootPath, "Pages", "Push");
        html = File.ReadAllText(Path.Combine(pushDir, "push.html"));
    }
    else
    {
        // In production, use embedded resources
        var assembly = typeof(Program).Assembly;
        html = ReadResource(assembly, "FeatherPod.Server.Pages.Push.push.html");
    }

    var pwaHead = pwaEnabled
        ? $$"""
            <link rel="manifest" href="/{{feedId}}/push/manifest.json">
            <script>
            if ('serviceWorker' in navigator) {
                navigator.serviceWorker.register('/{{feedId}}/push/sw.js', { scope: '/{{feedId}}/push' });
            }
            </script>
            """
        : "";

    return html
        .Replace("{{PWA_HEAD}}", pwaHead)
        .Replace("{{FEED_ID}}", feedId)
        .Replace("{{FEED_TITLE}}", escapedTitle)
        .Replace("{{ICON_CACHE_BUSTER}}", iconCacheBuster)
        .Replace("{{DROP_ZONE_CLASS}}", hasArtwork ? " drop-zone--has-artwork" : "")
        .Replace("{{BACKDROP_SRC}}", hasArtwork ? $" src=\"/{feedId}/icon.png{iconCacheBuster}\"" : "")
        .Replace("{{ICON_ETAG}}", iconETag ?? "")
        .Replace("{{IS_DEV}}", env.IsDevelopment().ToString().ToLowerInvariant())
        .Replace("{{PROGRESS_SMOOTHING}}", progressSmoothing.ToString().ToLowerInvariant());
}

static string IconCacheBuster(string? iconETag) => iconETag != null ? $"?v={Uri.EscapeDataString(iconETag)}" : "";

static string ReadResource(System.Reflection.Assembly assembly, string name)
{
    using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource '{name}' not found");
    using var reader = new StreamReader(stream);

    return reader.ReadToEnd();
}
