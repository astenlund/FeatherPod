using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FeatherPod.Server.Hubs;
using FeatherPod.Server.Middleware;
using FeatherPod.Server.Services;
using FeatherPod.Server.Models;
using FeatherPod.Server.Validation;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

class Program
{
    const long MaxUploadSizeBytes = 500 * 1024 * 1024; // 500 MB

    static readonly ConcurrentDictionary<string, (string Content, string ETag)> s_pushAssetCache = new();

    static async Task Main(string[] args)
    {
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

        // Application Insights (picks up APPLICATIONINSIGHTS_CONNECTION_STRING automatically)
        builder.Services.AddApplicationInsightsTelemetry();

        // Add services
        builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
        builder.Services.AddSingleton<EpisodeService>();
        builder.Services.AddSingleton<IUserService, UserService>();
        builder.Services.AddSingleton<IJobService, JobService>();
        builder.Services.AddSingleton<IJobProgressChannel, JobProgressChannel>();
        builder.Services.AddSingleton<IFeedEventChannel, FeedEventChannel>();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IconResizeService>();
        builder.Services.AddSingleton<PushNotificationService>();
        if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(builder.Configuration["AzureOpenAI:Endpoint"]))
        {
            builder.Services.AddSingleton<IAiService, FakeAiService>();
        }
        else
        {
            builder.Services.AddSingleton<IAiService, AiService>();
        }

        // YouTube import services
        builder.Services.AddSingleton<FFmpegBinaryManager>();
        builder.Services.AddSingleton<YtDlpBinaryManager>();
        builder.Services.AddSingleton<YtDlpService>();
        builder.Services.AddSingleton<YouTubeCookieService>();
        builder.Services.AddSingleton(Channel.CreateUnbounded<YouTubeDownloadJob>());
        builder.Services.AddHostedService<YouTubeDownloadService>();

        // Add background services
        builder.Services.AddHostedService<BlobSyncBackgroundService>();
        builder.Services.AddHostedService<TempFileCleanupService>();

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
        app.MapGet("/health", async (EpisodeService episodeService, YouTubeCookieService cookieService) =>
            {
                try
                {
                    var feeds = await episodeService.GetFeedsAsync();
                    var hasCookies = await cookieService.HasCookiesAsync();

                    return Results.Ok(new
                    {
                        Status = "Healthy",
                        FeedCount = feeds.Count,
                        YouTubeCookies = hasCookies ? "Uploaded" : "Not uploaded",
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
        app.MapGet("/{feedId}/feed.xml", async (string feedId, EpisodeService service, HttpContext context) =>
            {
                if (!InputValidation.IsValidFeedId(feedId))
                {
                    return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
                }

                var snapshot = await service.GetFeedSnapshotAsync(feedId);
                if (snapshot == null)
                {
                    return Results.NotFound($"Feed '{feedId}' not found");
                }

                var (feed, episodes, version, lastModified) = snapshot.Value;
                var etag = $"\"{feedId}-{version}\"";

                // Truncate to second precision for HTTP date comparison
                var lastModifiedTruncated = new DateTime(lastModified.Year, lastModified.Month, lastModified.Day, lastModified.Hour, lastModified.Minute, lastModified.Second, DateTimeKind.Utc);

                void SetCacheHeaders()
                {
                    context.Response.Headers.ETag = etag;
                    context.Response.Headers.LastModified = lastModifiedTruncated.ToString("R");
                    context.Response.Headers.CacheControl = "public, max-age=60";
                }

                // Check If-None-Match (strip W/ weak prefix per RFC 9110)
                var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString().Trim();
                if (ifNoneMatch == "*" || string.Equals(ifNoneMatch, etag, StringComparison.Ordinal) || string.Equals(ifNoneMatch, $"W/{etag}", StringComparison.Ordinal))
                {
                    SetCacheHeaders();

                    return Results.StatusCode(304);
                }

                // Check If-Modified-Since
                if (DateTimeOffset.TryParseExact(context.Request.Headers.IfModifiedSince.ToString(), "R", null, System.Globalization.DateTimeStyles.None, out var ifModifiedSince) && lastModifiedTruncated <= ifModifiedSince.UtcDateTime)
                {
                    SetCacheHeaders();

                    return Results.StatusCode(304);
                }

                var xml = RssFeedGenerator.GenerateFeed(feed, baseUrl, episodes, lastModifiedTruncated);
                SetCacheHeaders();

                return Results.Content(xml, "application/xml");
            })
            .WithName("GetRssFeed")
            .Produces(200, contentType: "application/xml")
            .Produces(304)
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

        // Resized icon for PWA manifest and apple-touch-icon (180x180, 192x192, or 512x512)
        app.MapGet("/{feedId}/icon-{size:int}.png", async (string feedId, int size, IconResizeService iconResizeService, HttpContext context) =>
            {
                if (!InputValidation.IsValidFeedId(feedId))
                {
                    return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
                }

                if (!IconResizeService.IsValidSize(size))
                {
                    return Results.BadRequest(new { error = "Icon size must be 180, 192, or 512" });
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
                    var iconETag = await blobStorageService.GetIconETagAsync(feedId);
                    var vapidPublicKey = config["PushNotifications:VapidPublicKey"];

                    return Results.Content(GeneratePushPageHtml(feedId, feed.Title, env, progressSmoothing, iconETag, vapidPublicKey), "text/html");
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
                    id = $"/{feedId}/push",
                    name = $"Push to {feed.Title}",
                    short_name = shortName,
                    start_url = $"/{feedId}/push",
                    scope = $"/{feedId}/push",
                    display = "standalone",
                    orientation = "portrait",
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
                            text = "shared_text",
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

        // Push page static assets (JS, CSS) -- served from disk in dev (hot-reload), embedded resources in prod
        app.MapGet("/{feedId}/push/sw.js", (string feedId, IWebHostEnvironment env, HttpContext context) =>
            {
                context.Response.Headers["Service-Worker-Allowed"] = "/";

                return ServePushAsset(feedId, "push-sw.js", "application/javascript", env, context);
            })
            .WithName("GetPushServiceWorker")
            .Produces(200, contentType: "application/javascript")
            .Produces(400);

        app.MapGet("/{feedId}/push/app.js", (string feedId, IWebHostEnvironment env, HttpContext context) =>
                ServePushAsset(feedId, "push.js", "application/javascript", env, context))
            .WithName("GetPushAppJs")
            .Produces(200, contentType: "application/javascript")
            .Produces(400);

        app.MapGet("/{feedId}/push/app.css", (string feedId, IWebHostEnvironment env, HttpContext context) =>
                ServePushAsset(feedId, "push.css", "text/css", env, context))
            .WithName("GetPushAppCss")
            .Produces(200, contentType: "text/css")
            .Produces(400);

        app.MapGet("/{feedId}/push/modules/{fileName}", (string feedId, string fileName, IWebHostEnvironment env, HttpContext context) =>
                ServePushModuleAsset(feedId, fileName, env, context))
            .WithName("GetPushModule")
            .Produces(200, contentType: "application/javascript")
            .Produces(400)
            .Produces(404);

        // Push notification subscription management
        app.MapPost("/api/feeds/{feedId}/push-subscriptions",
                async (string feedId, [Microsoft.AspNetCore.Mvc.FromBody] PushSubscriptionRequest body, PushNotificationService pushService) =>
                {
                    if (!InputValidation.IsValidFeedId(feedId))
                    {
                        return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
                    }

                    if (!pushService.IsEnabled)
                    {
                        return Results.BadRequest(new { error = "Push notifications are not configured" });
                    }

                    if (!Uri.TryCreate(body.Endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme != "https")
                    {
                        return Results.BadRequest(new { error = "Endpoint must be an absolute HTTPS URL" });
                    }

                    await pushService.SubscribeAsync(feedId, body);

                    return Results.Ok(new { message = "Subscribed" });
                })
            .WithName("SubscribePushNotifications")
            .Produces(200)
            .Produces(400);

        app.MapDelete("/api/feeds/{feedId}/push-subscriptions",
                async (string feedId, [Microsoft.AspNetCore.Mvc.FromBody] PushUnsubscribeRequest body, PushNotificationService pushService) =>
                {
                    if (!InputValidation.IsValidFeedId(feedId))
                    {
                        return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
                    }

                    await pushService.UnsubscribeAsync(feedId, body.Endpoint);

                    return Results.Ok(new { message = "Unsubscribed" });
                })
            .WithName("UnsubscribePushNotifications")
            .Produces(200)
            .Produces(400);

        app.MapPost("/api/feeds/{feedId}/push-sessions",
                async (string feedId, [Microsoft.AspNetCore.Mvc.FromBody] PushSessionRequest body, PushNotificationService pushService) =>
                {
                    if (!InputValidation.IsValidFeedId(feedId))
                    {
                        return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
                    }

                    if (!pushService.IsEnabled)
                    {
                        return Results.BadRequest(new { error = "Push notifications are not configured" });
                    }

                    if (body.JobIds.Count == 0 && body.UploadsRemaining == 0)
                    {
                        return Results.BadRequest(new { error = "At least one jobId or uploadsRemaining > 0 is required" });
                    }

                    await pushService.TrackJobsAsync(feedId, body.JobIds, body.UploadsRemaining);

                    return Results.Ok(new { tracked = body.JobIds.Count, uploadsRemaining = body.UploadsRemaining });
                })
            .WithName("TrackPushSession")
            .Produces(200)
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

        // Transcript file serving
        app.MapGet("/{feedId}/transcripts/{episodeId}.vtt", async (string feedId, string episodeId, IBlobStorageService service) =>
            {
                if (!InputValidation.IsValidFeedId(feedId))
                {
                    return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
                }

                if (!InputValidation.IsValidEpisodeId(episodeId))
                {
                    return Results.BadRequest(new { error = "Invalid episode ID format" });
                }

                var stream = await service.DownloadTranscriptAsync(feedId, episodeId);
                if (stream == null)
                {
                    return Results.NotFound();
                }

                return Results.Stream(stream, "text/vtt");
            })
            .WithName("GetTranscript")
            .Produces(200)
            .Produces(404);

        app.Run();
    }

    // ============================================================================
    // PUSH PAGE HTML GENERATION
    // ============================================================================

    static string GeneratePushPageHtml(string feedId, string feedTitle, IWebHostEnvironment env, bool progressSmoothing, string? iconETag, string? vapidPublicKey)
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

        var pwaHead = hasArtwork
            ? $$"""
                <link rel="manifest" href="/{{feedId}}/push/manifest.json">
                <meta name="apple-mobile-web-app-capable" content="yes">
                <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
                <meta name="apple-mobile-web-app-title" content="{{escapedTitle}}">
                <link rel="apple-touch-icon" href="/{{feedId}}/icon-180.png{{iconCacheBuster}}">
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
            .Replace("{{PROGRESS_SMOOTHING}}", progressSmoothing.ToString().ToLowerInvariant())
            .Replace("{{VAPID_PUBLIC_KEY}}", vapidPublicKey ?? "");
    }

    static string IconCacheBuster(string? iconETag) => iconETag != null ? $"?v={Uri.EscapeDataString(iconETag)}" : "";

    static IResult ServePushAsset(string feedId, string fileName, string contentType, IWebHostEnvironment env, HttpContext context)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        string content;
        string etag;
        if (env.IsDevelopment())
        {
            var pushDir = Path.Combine(env.ContentRootPath, "Pages", "Push");
            content = File.ReadAllText(Path.Combine(pushDir, fileName));
            etag = ComputeWeakETag(content);
        }
        else
        {
            var resourceName = $"FeatherPod.Server.Pages.Push.{fileName}";
            (content, etag) = s_pushAssetCache.GetOrAdd(resourceName, name =>
            {
                var c = ReadResource(typeof(Program).Assembly, name);

                return (c, ComputeWeakETag(c));
            });
        }

        if (IsNotModified(context, etag))
        {
            return Results.StatusCode(304);
        }

        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        return Results.Content(content, contentType);
    }

    static IResult ServePushModuleAsset(string feedId, string fileName, IWebHostEnvironment env, HttpContext context)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return Results.BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        if (!fileName.EndsWith(".js", StringComparison.Ordinal) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            return Results.NotFound();
        }

        string content;
        string etag;
        if (env.IsDevelopment())
        {
            var modulePath = Path.Combine(env.ContentRootPath, "Pages", "Push", "modules", fileName);
            if (!File.Exists(modulePath))
            {
                return Results.NotFound();
            }

            content = File.ReadAllText(modulePath);
            etag = ComputeWeakETag(content);
        }
        else
        {
            var resourceName = $"FeatherPod.Server.Pages.Push.modules.{fileName}";
            if (!s_pushAssetCache.TryGetValue(resourceName, out var cached))
            {
                var assembly = typeof(Program).Assembly;
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    return Results.NotFound();
                }

                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
                etag = ComputeWeakETag(content);
                s_pushAssetCache.TryAdd(resourceName, (content, etag));
            }
            else
            {
                (content, etag) = cached;
            }
        }

        if (IsNotModified(context, etag))
        {
            return Results.StatusCode(304);
        }

        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";

        return Results.Content(content, "application/javascript");
    }

    static string ReadResource(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource '{name}' not found");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    static string ComputeWeakETag(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));

        return $"W/\"{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}\"";
    }

    static bool IsNotModified(HttpContext context, string etag)
    {
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString().Trim();
        if (ifNoneMatch.Length > 0
            && (string.Equals(ifNoneMatch, etag, StringComparison.Ordinal)
                || string.Equals(ifNoneMatch, etag[2..], StringComparison.Ordinal)))
        {
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "no-cache";

            return true;
        }

        return false;
    }
}
