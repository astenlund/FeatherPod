using FeatherPod.Cli.Settings;
using FeatherPod.Models;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FeatherPod.Cli.Infrastructure;

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

    internal static async Task<FeedConfig?> SelectFeedAsync(HttpClient httpClient, string environment, bool forcePrompt = false)
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
        var lastUsedFeed = feeds.FirstOrDefault(f => f.Id == lastUsed);

        if (!forcePrompt && lastUsedFeed != null)
        {
            return lastUsedFeed;
        }

        // Show feed selector
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle(lastUsedFeed == null && !string.IsNullOrEmpty(lastUsed)
                ? "Previous feed no longer exists. Select feed:"
                : "Select feed:")
            .WithHint("(arrow keys, Enter to select)")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(
                null,
                $"[cyan]{Markup.Escape(feed.Id)}[/] - {Markup.Escape(feed.Title)}",
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
            var json = JsonSerializer.Serialize(feedConfig, JsonSerializerOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("/api/feeds", content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var createdFeed = JsonSerializer.Deserialize<FeedConfig>(responseContent, JsonSerializerOptions);

                AnsiConsole.MarkupLine($"[green]✓[/] Created feed: [cyan]{Markup.Escape(id)}[/]");
                AnsiConsole.WriteLine();
                return createdFeed;
            }
            else
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
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating feed:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            return null;
        }
    }

    internal static async Task RenameFeedAsync(HttpClient httpClient)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds to rename.[/]");
            AnsiConsole.WriteLine();
            return;
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
            return;
        }

        var newId = AnsiConsole.Ask<string>("New feed ID:");

        try
        {
            var response = await httpClient.PostAsync($"/api/feeds/{selectedFeed.Id}/rename?newId={Uri.EscapeDataString(newId)}", null);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Renamed feed from [cyan]{Markup.Escape(selectedFeed.Id)}[/] to [cyan]{Markup.Escape(newId)}[/]");
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
    }

    internal static async Task DeleteFeedAsync(HttpClient httpClient)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds to delete.[/]");
            AnsiConsole.WriteLine();
            return;
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
            return;
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
            return;
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{selectedFeed.Id}");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted feed: [cyan]{Markup.Escape(selectedFeed.Id)}[/]");
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
    }

    internal static async Task ManageFeedsAsync(HttpClient httpClient)
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
                    await CreateFeedAsync(httpClient);
                    break;
                case "rename":
                    await RenameFeedAsync(httpClient);
                    break;
                case "delete":
                    await DeleteFeedAsync(httpClient);
                    break;
            }
        }
    }

    // ============================================================================
    // EPISODE MANAGEMENT
    // ============================================================================

    internal static async Task ListEpisodesAsync(HttpClient httpClient, FeedConfig feed)
    {
        try
        {
            var response = await httpClient.GetAsync($"/{feed.Id}/api/episodes");
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
            var response = await httpClient.GetAsync($"/{feed.Id}/api/episodes");
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

            var deleteResponse = await httpClient.DeleteAsync($"/{feed.Id}/api/episodes/{episodeToDelete.Id}");

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

    internal static async Task<bool> UploadEpisodeAsync(HttpClient httpClient, FeedConfig feed, string filePath, PushSettings settings)
    {
        var fileName = Path.GetFileName(filePath);
        var success = false;

        await AnsiConsole.Status()
            .StartAsync($"Uploading [cyan]{fileName}[/]...", async _ =>
            {
                try
                {
                    // Create multipart form data
                    using var content = new MultipartFormDataContent();

                    // Add file
                    var fileBytes = await File.ReadAllBytesAsync(filePath);
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

                    // Add published date options
                    if (!string.IsNullOrEmpty(settings.PublishedDate))
                    {
                        content.Add(new StringContent(settings.PublishedDate), "publishedDate");
                    }
                    else if (settings.ExtractDateFromFile == true)
                    {
                        content.Add(new StringContent("true"), "useMetadataForPublishedDate");
                    }
                    // Note: UseCurrentDate is the default behavior, no need to send anything

                    // Upload
                    var response = await httpClient.PostAsync($"/{feed.Id}/api/episodes", content);

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
}
