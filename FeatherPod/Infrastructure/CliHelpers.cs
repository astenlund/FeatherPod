using System.Net.Http.Headers;
using System.Text.Json;
using FeatherPod.Server.Models;
using FeatherPod.Settings.Episode;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class CliHelpers
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string? GetEnvironment(string? environment, bool useDefault = true)
    {
        if (string.IsNullOrEmpty(environment))
        {
            if (useDefault)
            {
                environment = "Prod";
            }
            else
            {
                environment = SelectEnvironment();
                if (environment == null) return null;
            }
        }

        if (environment != "Dev" && environment != "Test" && environment != "Prod")
        {
            AnsiConsole.MarkupLine($"[red]Invalid environment:[/] {environment}");
            AnsiConsole.MarkupLine("Valid options: Dev, Test, Prod");
            return null;
        }

        AnsiConsole.MarkupLine($"Environment: [cyan]{environment}[/]");
        AnsiConsole.WriteLine();

        return environment;
    }

    internal static string? SelectEnvironment()
    {
        return new MenuBuilder<string?>()
            .WithTitle("Select environment:")
            .WithHint("(arrow keys, Enter to select)")
            .AddOption("D", "Dev - Local (localhost:8080 with Azurite)", "Dev")
            .AddOption("T", "Test - featherpod-test.azurewebsites.net", "Test")
            .AddOption("P", "Prod - featherpod.azurewebsites.net", "Prod")
            .AllowCancel()
            .Show();
    }

    internal static async Task<(HttpClient?, IConfiguration?)> SetupHttpClientAsync(string environment)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var apiBaseUrl = configuration["Api:BaseUrl"]
            ?? throw new InvalidOperationException("Api:BaseUrl not configured in appsettings.json");

        var apiKey = configuration["Api:ApiKey"];

        if (string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] API key not configured.");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Option 1[/] (Recommended): Create a local settings file:");
            AnsiConsole.MarkupLine($"  File: [cyan]appsettings.{environment}.Local.json[/]");
            AnsiConsole.MarkupLine("  Content: { \"Api\": { \"ApiKey\": \"your-api-key-here\" } }");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Option 2[/]: Set environment variable:");
            AnsiConsole.MarkupLine("  [grey]$env:Api__ApiKey = \"your-api-key-here\"  (PowerShell)[/]");
            AnsiConsole.MarkupLine("  [grey]export Api__ApiKey=\"your-api-key-here\"  (Bash)[/]");
            return (null, null);
        }

        AnsiConsole.MarkupLine($"API: [cyan]{apiBaseUrl}/api[/]");

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl)
        };
        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Testing API connection...", async _ =>
                {
                    var response = await httpClient.GetAsync("/api/feeds");
                    response.EnsureSuccessStatusCode();
                });

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]✓[/] Connected");
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Connection failed");
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Make sure the FeatherPod API is running and accessible.");
            return (null, null);
        }

        return (httpClient, configuration);
    }

    // ============================================================================
    // FEED MANAGEMENT
    // ============================================================================

    internal static async Task<List<FeedConfig>> GetFeedsAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/feeds");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<FeedConfig>>(json, JsonSerializerOptions) ?? [];
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching feeds:[/] {ex.Message}");
            return [];
        }
    }

    internal static async Task<FeedConfig?> GetFeedByIdAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feedId}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<FeedConfig>(json, JsonSerializerOptions);
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching feed:[/] {ex.Message}");
            return null;
        }
    }

    internal static async Task<FeedConfig?> SelectFeedAsync(HttpClient httpClient, string environment, bool forcePrompt = false, string? contextMessage = null)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds found.[/] Use [cyan]'M: Manage Feeds'[/] to create one.");
            AnsiConsole.WriteLine();
            return null;
        }

        if (feeds.Count == 1 && !forcePrompt)
        {
            return feeds[0];
        }

        // Multiple feeds - check last-used
        var lastUsed = UserSettings.Default.GetLastUsedFeed(environment);
        var lastUsedFeed = string.IsNullOrEmpty(lastUsed) ? null : feeds.FirstOrDefault(f => f.Id == lastUsed);

        if (!forcePrompt && lastUsedFeed != null)
        {
            return lastUsedFeed;
        }

        // Show feed selector with optional context message
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle($"{contextMessage} Select feed:".Trim())
            .WithHint("(arrow keys, Enter to select)")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(
                null,
                $"[cyan]{Markup.Escape(feed.Title)}[/]",
                feed
            );
        }

        var selected = menu.Show();
        if (selected != null)
        {
            UserSettings.Default.SetLastUsedFeed(environment, selected.Id);
        }

        return selected;
    }

    internal static async Task<FeedConfig?> CreateFeedAsync(HttpClient httpClient)
    {
        var id = AnsiConsole.Ask<string>("Feed ID (URL-friendly slug):");
        var title = AnsiConsole.Ask<string>("Title:");
        var description = AnsiConsole.Ask("Description (optional):", string.Empty);
        var author = AnsiConsole.Ask<string>("Author:");
        var email = AnsiConsole.Ask("Email (optional):", string.Empty);
        var language = AnsiConsole.Ask("Language:", "en");
        var category = AnsiConsole.Ask("Category (optional):", string.Empty);
        var iconPath = AnsiConsole.Ask("Icon path (optional, PNG/JPEG):", string.Empty);
        iconPath = iconPath.Trim().Trim('"', '\''); // Remove surrounding quotes

        var feedConfig = new FeedConfig
        {
            Id = id,
            Title = title,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Author = author,
            Email = string.IsNullOrEmpty(email) ? null : email,
            Language = language,
            Category = string.IsNullOrEmpty(category) ? null : category
        };

        try
        {
            // Create the feed first
            var json = JsonSerializer.Serialize(feedConfig, JsonSerializerOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("/api/feeds", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed to create feed:[/] {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
                AnsiConsole.WriteLine();
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var createdFeed = JsonSerializer.Deserialize<FeedConfig>(responseContent, JsonSerializerOptions);

            AnsiConsole.MarkupLine($"[green]✓[/] Created feed: [cyan]{Markup.Escape(id)}[/]");

            // Upload icon if provided
            if (!string.IsNullOrEmpty(iconPath))
            {
                if (!File.Exists(iconPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Icon file not found: {Markup.Escape(iconPath)}");
                }
                else
                {
                    await UploadIconAsync(httpClient, id, iconPath);
                }
            }

            AnsiConsole.WriteLine();
            return createdFeed;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating feed:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            return null;
        }
    }

    internal static async Task<bool> UploadIconAsync(HttpClient httpClient, string feedId, string iconPath)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            AnsiConsole.MarkupLine($"[grey]Uploading icon to: {Markup.Escape(url)}[/]");
            AnsiConsole.MarkupLine($"[grey]Base URL: {Markup.Escape(httpClient.BaseAddress?.ToString() ?? "null")}[/]");

            await using var fileStream = File.OpenRead(iconPath);
            using var formData = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);

            fileContent.Headers.ContentType = new("image/png");
            formData.Add(fileContent, "file", Path.GetFileName(iconPath));

            var response = await httpClient.PostAsync(url, formData);

            AnsiConsole.MarkupLine($"[grey]Response status: {response.StatusCode}[/]");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Uploaded icon");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to upload icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error uploading icon: {ex.Message}");
            return false;
        }
    }

    internal static async Task<bool> DeleteIconAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            var response = await httpClient.DeleteAsync(url);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Removed icon");
                return true;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[yellow]⚠[/] {errorContent}");
                return false;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting icon: {ex.Message}");
            return false;
        }
    }

    internal static async Task<(bool wasRenamed, string? oldId, FeedConfig? renamedFeed)> RenameFeedAsync(HttpClient httpClient)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds to rename.[/]");
            AnsiConsole.WriteLine();
            return (false, null, null);
        }

        // Select feed to rename
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle("Select feed to rename:")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(null, $"[cyan]{Markup.Escape(feed.Id)}[/] - {Markup.Escape(feed.Title)}", feed);
        }

        var selectedFeed = menu.Show();
        if (selectedFeed == null)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            AnsiConsole.WriteLine();
            return (false, null, null);
        }

        var oldId = selectedFeed.Id;
        var newId = AnsiConsole.Ask<string>("New feed ID:");

        try
        {
            var response = await httpClient.PostAsync($"/api/feeds/{selectedFeed.Id}/rename?newId={Uri.EscapeDataString(newId)}", null);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Renamed feed from [cyan]{Markup.Escape(selectedFeed.Id)}[/] to [cyan]{Markup.Escape(newId)}[/]");
                AnsiConsole.WriteLine();

                // Fetch the renamed feed
                var updatedFeed = await GetFeedByIdAsync(httpClient, newId);
                return (true, oldId, updatedFeed);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed to rename feed:[/] {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error renaming feed:[/] {ex.Message}");
        }

        AnsiConsole.WriteLine();
        return (false, null, null);
    }

    internal static async Task<(bool wasDeleted, string? deletedFeedId)> DeleteFeedAsync(HttpClient httpClient)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds to delete.[/]");
            AnsiConsole.WriteLine();
            return (false, null);
        }

        // Select feed to delete
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle("Select feed to [red]delete[/]:")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(null, $"[cyan]{Markup.Escape(feed.Id)}[/] - {Markup.Escape(feed.Title)}", feed);
        }

        var selectedFeed = menu.Show();
        if (selectedFeed == null)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            AnsiConsole.WriteLine();
            return (false, null);
        }

        // Confirm deletion
        var confirmed = new MenuBuilder<bool?>()
            .WithTitle($"[red]Delete feed[/] [cyan]{Markup.Escape(selectedFeed.Id)}[/] and all its episodes?")
            .WithHint("(arrow keys or Y/N, Esc to cancel)")
            .AddOption("Y", "Yes", true)
            .AddOption("N", "No", false)
            .AllowCancel(true, false)
            .Show();

        if (confirmed != true)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            AnsiConsole.WriteLine();
            return (false, null);
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{selectedFeed.Id}");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted feed: [cyan]{Markup.Escape(selectedFeed.Id)}[/]");
                AnsiConsole.WriteLine();
                return (true, selectedFeed.Id);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed to delete feed:[/] {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error deleting feed:[/] {ex.Message}");
        }

        AnsiConsole.WriteLine();
        return (false, null);
    }

    internal static async Task<FeedManagementResult> ManageFeedsAsync(HttpClient httpClient)
    {
        while (true)
        {
            var choice = new MenuBuilder<string?>()
                .WithTitle("Manage Feeds:")
                .WithHint("(arrow keys or C/R/D, Esc to go back)")
                .AddOption("C", "Create new feed", "create")
                .AddOption("R", "Rename feed", "rename")
                .AddOption("D", "Delete feed", "delete")
                .AllowCancel()
                .Show();

            if (choice == null) break;

            switch (choice)
            {
                case "create":
                    var newFeed = await CreateFeedAsync(httpClient);
                    return new FeedManagementResult { CreatedFeed = newFeed };
                case "rename":
                    var (wasRenamed, oldId, renamedFeed) = await RenameFeedAsync(httpClient);
                    if (wasRenamed)
                    {
                        return new FeedManagementResult { RenamedFeed = renamedFeed, OldFeedId = oldId };
                    }
                    break;
                case "delete":
                    var (wasDeleted, deletedFeedId) = await DeleteFeedAsync(httpClient);
                    if (wasDeleted)
                    {
                        return new FeedManagementResult { DeletedFeedId = deletedFeedId };
                    }
                    break;
            }
        }

        return new FeedManagementResult(); // User cancelled (pressed Esc)
    }

    internal record FeedManagementResult
    {
        public FeedConfig? CreatedFeed { get; init; }
        public FeedConfig? RenamedFeed { get; init; }
        public string? OldFeedId { get; init; }
        public string? DeletedFeedId { get; init; }
    }

    // ============================================================================
    // EPISODE MANAGEMENT
    // ============================================================================

    internal static async Task ListEpisodesAsync(HttpClient httpClient, FeedConfig feed)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feed.Id}/episodes");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonSerializerOptions) ?? [];

            AnsiConsole.MarkupLine($"Feed: [cyan]{Markup.Escape(feed.Title)}[/]");
            AnsiConsole.WriteLine();

            if (episodes.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No episodes found.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("Published");
            table.AddColumn("Title");
            table.AddColumn("URL");
            table.AddColumn("Size");
            table.AddColumn("Duration");

            for (var i = 0; i < episodes.Count; i++)
            {
                var episode = episodes[i];
                var formattedDate = episode.PublishedDate.ToString("yyyy-MM-dd HH:mm");
                var formattedSize = FormatFileSize(episode.FileSize);
                var formattedDuration = FormatDuration(episode.Duration);

                table.AddRow(
                    $"[grey]{i + 1}[/]",
                    $"[grey]{formattedDate}[/]",
                    Markup.Escape(episode.Title),
                    $"[cyan]{Markup.Escape(episode.Url ?? string.Empty)}[/]",
                    formattedSize,
                    formattedDuration
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]Total: {episodes.Count} episodes[/]");
            AnsiConsole.WriteLine();
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching episodes:[/] {ex.Message}");
        }
    }

    private static bool ConfirmDelete(Episode episode)
    {
        var result = new MenuBuilder<bool?>()
            .WithTitle($"[red]Delete[/] {Markup.Escape(episode.Title)}?")
            .WithHint("(arrow keys or Y/N, Esc to cancel)")
            .AddOption("Y", "Yes", true)
            .AddOption("N", "No", false)
            .AllowCancel(true, false)
            .Show();

        return result ?? false;
    }

    private static int SelectEpisode(List<Episode> episodes)
    {
        var menu = new MenuBuilder<int?>()
            .WithTitle("Select episode to [red]delete[/]:")
            .AllowCancel();

        for (var i = 0; i < episodes.Count; i++)
        {
            var episode = episodes[i];
            menu.AddOption(
                null,
                "", // Label will be generated by formatter
                i,
                _ => $"[grey]({episode.PublishedDate:yyyy-MM-dd})[/] {Markup.Escape(episode.Title)}"
            );
        }

        return menu.Show() ?? -1;
    }

    internal static async Task DeleteEpisodeAsync(HttpClient httpClient, FeedConfig feed)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feed.Id}/episodes");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonSerializerOptions) ?? [];

            if (episodes.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No episodes to delete.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            // Use custom selector with Escape support
            var selectedIndex = SelectEpisode(episodes);

            if (selectedIndex == -1)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            var episodeToDelete = episodes[selectedIndex];

            // Custom confirmation with Escape support
            var confirmed = ConfirmDelete(episodeToDelete);
            if (!confirmed)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return;
            }

            var deleteResponse = await httpClient.DeleteAsync($"/api/feeds/{feed.Id}/episodes/{episodeToDelete.Id}");

            if (deleteResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted: {Markup.Escape(episodeToDelete.Title)}");
            }
            else if (deleteResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Episode not found (may have already been deleted).[/]");
            }
            else if (deleteResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                AnsiConsole.MarkupLine("[red]Unauthorized:[/] Check your API key configuration.");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to delete episode:[/] {deleteResponse.StatusCode}");
                var errorContent = await deleteResponse.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error deleting episode:[/] {ex.Message}");
        }
    }

    internal static List<string> ExpandFilePatterns(string input)
    {
        var result = new List<string>();

        // Split by comma for comma-separated lists
        var patterns = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pattern in patterns)
        {
            // Check if pattern contains wildcards
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                // Get directory and search pattern
                var directory = Path.GetDirectoryName(pattern);
                var searchPattern = Path.GetFileName(pattern);

                // If no directory specified, use current directory
                if (string.IsNullOrEmpty(directory))
                    directory = Directory.GetCurrentDirectory();

                if (Directory.Exists(directory))
                {
                    var matchingFiles = Directory.GetFiles(directory, searchPattern);
                    result.AddRange(matchingFiles);
                }
            }
            else
            {
                // Regular file path
                if (File.Exists(pattern))
                {
                    result.Add(pattern);
                }
            }
        }

        return result;
    }

    internal static async Task<bool> UploadEpisodeAsync(HttpClient httpClient, IConfiguration configuration, FeedConfig feed, string filePath, PushSettings settings)
    {
        var fileName = Path.GetFileName(filePath);
        var success = false;
        string? normalizedTempFile = null;

        // Extract creation time from ORIGINAL file if requested (before normalization)
        string? extractedPublishedDate = null;
        if (settings.ExtractDateFromFile == true)
        {
            var creationTime = FFmpegService.ExtractCreationTime(filePath);
            if (creationTime.HasValue)
            {
                extractedPublishedDate = creationTime.Value.ToString("o"); // ISO 8601 format
            }
        }

        try
        {
            // Load audio normalization configuration
            var normalizationConfig = new AudioNormalizationConfig();
            configuration.GetSection("AudioNormalization").Bind(normalizationConfig);

            // Check if normalization is enabled
            var fileToUpload = filePath;
            if (normalizationConfig.Enabled)
            {
                // Check if FFmpeg is available
                if (!FFmpegService.IsFFmpegAvailable())
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] FFmpeg is not installed or not found in PATH.");
                    AnsiConsole.MarkupLine("  Audio normalization requires FFmpeg to be installed.");
                    AnsiConsole.MarkupLine("  Download from: [link]https://ffmpeg.org/download.html[/]");
                    AnsiConsole.WriteLine();

                    var continueWithoutNormalization = new MenuBuilder<bool?>()
                        .WithTitle("Continue upload without normalization?")
                        .WithHint("(arrow keys or Y/N, Esc to cancel)")
                        .AddOption("Y", "Yes - Upload original file", true)
                        .AddOption("N", "No - Cancel upload", false)
                        .AllowCancel(true, false)
                        .Show();

                    if (continueWithoutNormalization != true)
                    {
                        AnsiConsole.MarkupLine("[grey]Upload cancelled.[/]");
                        return false;
                    }
                    AnsiConsole.WriteLine();
                }
                else
                {
                    // Normalize audio
                    AnsiConsole.MarkupLine($"Normalizing [cyan]{fileName}[/] to -16 LUFS...");
                    normalizedTempFile = await FFmpegService.NormalizeAudioAsync(filePath, normalizationConfig);

                    if (normalizedTempFile == null)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[yellow]Warning:[/] Audio normalization failed.");

                        var continueWithOriginal = new MenuBuilder<bool?>()
                            .WithTitle("Continue upload with original (unnormalized) file?")
                            .WithHint("(arrow keys or Y/N, Esc to cancel)")
                            .AddOption("Y", "Yes - Upload original file", true)
                            .AddOption("N", "No - Cancel upload", false)
                            .AllowCancel(true, false)
                            .Show();

                        if (continueWithOriginal != true)
                        {
                            AnsiConsole.MarkupLine("[grey]Upload cancelled.[/]");
                            return false;
                        }
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        fileToUpload = normalizedTempFile;
                        AnsiConsole.MarkupLine("[green]✓[/] Audio normalized successfully");
                        AnsiConsole.WriteLine();
                    }
                }
            }

            await AnsiConsole.Status()
                .StartAsync($"Uploading [cyan]{fileName}[/]...", async _ =>
                {
                    try
                    {
                        // Create multipart form data
                        using var content = new MultipartFormDataContent();

                        // Add file (use normalized file if available, otherwise original)
                        var fileBytes = await File.ReadAllBytesAsync(fileToUpload);
                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                        content.Add(fileContent, "file", fileName);

                        // Add optional title
                        if (!string.IsNullOrEmpty(settings.Title))
                        {
                            content.Add(new StringContent(settings.Title), "title");
                        }

                        // Add optional description
                        if (!string.IsNullOrEmpty(settings.Description))
                        {
                            content.Add(new StringContent(settings.Description), "description");
                        }

                        // Add optional summary
                        if (!string.IsNullOrEmpty(settings.Summary))
                        {
                            content.Add(new StringContent(settings.Summary), "summary");
                        }

                        // Add published date options
                        if (!string.IsNullOrEmpty(settings.PublishedDate))
                        {
                            content.Add(new StringContent(settings.PublishedDate), "publishedDate");
                        }
                        else if (!string.IsNullOrEmpty(extractedPublishedDate))
                        {
                            // Use extracted date from original file (before normalization)
                            content.Add(new StringContent(extractedPublishedDate), "publishedDate");
                        }
                        // Note: UseCurrentDate is the default behavior, no need to send anything

                        // Upload
                        var response = await httpClient.PostAsync($"/api/feeds/{feed.Id}/episodes", content);

                        if (response.IsSuccessStatusCode)
                        {
                            success = true;
                            var responseContent = await response.Content.ReadAsStringAsync();
                            var episode = JsonSerializer.Deserialize<Episode>(responseContent, JsonSerializerOptions);

                            AnsiConsole.MarkupLine($"[green]✓[/] Uploaded: [cyan]{fileName}[/]");
                            AnsiConsole.WriteLine();

                            if (episode != null)
                            {
                                AnsiConsole.MarkupLine($"  ID: [grey]{episode.Id}[/]");
                                AnsiConsole.MarkupLine($"  Title: {Markup.Escape(episode.Title)}");
                                AnsiConsole.MarkupLine($"  Published: [grey]{episode.PublishedDate:yyyy-MM-dd HH:mm:ss}[/]");
                                AnsiConsole.MarkupLine($"  Duration: [grey]{FormatDuration(episode.Duration)}[/]");
                                AnsiConsole.MarkupLine($"  Size: [grey]{FormatFileSize(episode.FileSize)}[/]");
                            }
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Unauthorized: Check your API key configuration");
                        }
                        else
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            AnsiConsole.MarkupLine($"[red]✗[/] Failed to upload [cyan]{fileName}[/]: {response.StatusCode}");
                            if (!string.IsNullOrEmpty(errorContent))
                            {
                                AnsiConsole.MarkupLine($"  [red]Error:[/] {errorContent}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Error uploading [cyan]{fileName}[/]: {ex.Message}");
                    }
                });

            return success;
        }
        finally
        {
            // Clean up normalized temp file
            if (normalizedTempFile != null && File.Exists(normalizedTempFile))
            {
                try
                {
                    File.Delete(normalizedTempFile);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }

    // ============================================================================
    // Episode Move/Copy Helpers
    // ============================================================================

    internal static async Task<List<Episode>?> GetEpisodesAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feedId}/episodes");

            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Failed to fetch episodes from feed '{feedId}'");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonSerializerOptions);
            return episodes;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching episodes:[/] {ex.Message}");
            return null;
        }
    }

    internal static async Task<bool> MoveEpisodeAsync(HttpClient httpClient, string fromFeed, string episodeId, string toFeed)
    {
        try
        {
            var requestBody = new { targetFeedId = toFeed };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"/api/feeds/{fromFeed}/episodes/{episodeId}/move", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                var errorObj = JsonSerializer.Deserialize<JsonElement>(errorJson);
                var errorMsg = errorObj.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
                AnsiConsole.MarkupLine($"[red]Error:[/] {errorMsg}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error moving episode:[/] {ex.Message}");
            return false;
        }
    }

    internal static async Task<bool> CopyEpisodeAsync(HttpClient httpClient, string fromFeed, string episodeId, string toFeed)
    {
        try
        {
            var requestBody = new { targetFeedId = toFeed };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"/api/feeds/{fromFeed}/episodes/{episodeId}/copy", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                var errorObj = JsonSerializer.Deserialize<JsonElement>(errorJson);
                var errorMsg = errorObj.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
                AnsiConsole.MarkupLine($"[red]Error:[/] {errorMsg}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error copying episode:[/] {ex.Message}");
            return false;
        }
    }

    internal static List<Episode> SelectEpisodesMulti(List<Episode> episodes)
    {
        if (episodes.Count == 0)
        {
            return [];
        }

        var prompt = new MultiSelectionPrompt<Episode>()
            .Title("Select episodes:")
            .PageSize(10)
            .Required()
            .MoreChoicesText("[grey](Move up/down for more)[/]")
            .InstructionsText("[grey]([blue]Space[/] to toggle, [green]Enter[/] to confirm)[/]")
            .UseConverter(ep => $"[grey]{ep.PublishedDate:yyyy-MM-dd}[/] {Markup.Escape(ep.Title)}")
            .AddChoices(episodes);

        return AnsiConsole.Prompt(prompt);
    }

    internal static List<Episode> MatchEpisodesByPattern(List<Episode> episodes, string pattern)
    {
        // Try exact ID match first
        var exactMatch = episodes.FirstOrDefault(e => e.Id == pattern);
        if (exactMatch != null) return [exactMatch];

        // Wildcard match on filename or title (case-insensitive)
        if (pattern == "*") return episodes;

        var lower = pattern.ToLower();

        // Contains pattern: *text*
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
        {
            var contains = lower.Trim('*');
            return episodes.Where(e =>
                e.FileName.ToLower().Contains(contains) ||
                e.Title.ToLower().Contains(contains)).ToList();
        }

        // Prefix pattern: text*
        if (pattern.EndsWith("*"))
        {
            var prefix = lower[..^1];
            return episodes.Where(e =>
                e.FileName.ToLower().StartsWith(prefix) ||
                e.Title.ToLower().StartsWith(prefix)).ToList();
        }

        // Suffix pattern: *text
        if (pattern.StartsWith("*"))
        {
            var suffix = lower[1..];
            return episodes.Where(e =>
                e.FileName.ToLower().EndsWith(suffix) ||
                e.Title.ToLower().EndsWith(suffix)).ToList();
        }

        // Literal match (no wildcards) - match filename or title
        return episodes.Where(e =>
            e.FileName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
            e.Title.Equals(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
