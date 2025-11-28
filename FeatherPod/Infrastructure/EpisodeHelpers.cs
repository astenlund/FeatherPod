using System.Net.Http.Headers;
using System.Text.Json;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Settings.Episode;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class EpisodeHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
            // Load audio normalization configuration from appsettings (for LUFS target, etc.)
            var normalizationConfig = new AudioNormalizationConfig();
            configuration.GetSection("AudioNormalization").Bind(normalizationConfig);

            // Get normalization enabled from user preferences (defaults to true)
            var userPref = PreferencesHelpers.GetNormalizationEnabled();
            normalizationConfig.Enabled = userPref ?? true;

            // Check if normalization is enabled
            var fileToUpload = filePath;
            if (normalizationConfig.Enabled)
            {
                // Check if FFmpeg is available
                if (!FFmpegService.IsFFmpegAvailable())
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] FFmpeg is not installed or not found.");

                    // Offer to download FFmpeg automatically
                    var downloadChoice = new MenuBuilder<bool?>()
                        .WithTitle("Download FFmpeg automatically?")
                        .WithHint("(arrow keys or Y/N, Esc to cancel)")
                        .AddOption("Y", "Yes - Download FFmpeg (~100MB)", true)
                        .AddOption("N", "No - Skip normalization", false)
                        .AllowCancel(true, false)
                        .Show();

                    if (downloadChoice == true)
                    {
                        AnsiConsole.WriteLine();
                        var downloadSuccess = await DownloadFFmpegWithProgressAsync();

                        if (!downloadSuccess)
                        {
                            AnsiConsole.MarkupLine("[red]Failed to download FFmpeg.[/]");
                            AnsiConsole.WriteLine();

                            var continueAfterFailure = new MenuBuilder<bool?>()
                                .WithTitle("Continue upload without normalization?")
                                .WithHint("(arrow keys or Y/N, Esc to cancel)")
                                .AddOption("Y", "Yes - Upload original file", true)
                                .AddOption("N", "No - Cancel upload", false)
                                .AllowCancel(true, false)
                                .Show();

                            if (continueAfterFailure != true)
                            {
                                AnsiConsole.MarkupLine("[grey]Upload cancelled.[/]");
                                return false;
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[green]✓[/] FFmpeg downloaded successfully");
                        }

                        AnsiConsole.WriteLine();
                    }
                    else if (downloadChoice == false)
                    {
                        // User chose not to download, continue without normalization
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        // User cancelled (Esc)
                        AnsiConsole.MarkupLine("[grey]Upload cancelled.[/]");
                        return false;
                    }
                }

                // Re-check availability after potential download
                if (FFmpegService.IsFFmpegAvailable())
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
                    }
                    else
                    {
                        fileToUpload = normalizedTempFile;
                        AnsiConsole.MarkupLine("[green]✓[/] Audio normalized successfully");
                    }

                    AnsiConsole.WriteLine();
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
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(AudioHelper.GetMimeType(fileName));
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
                            var episode = JsonSerializer.Deserialize<Episode>(responseContent, JsonOptions);

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

    internal static string FormatFileSize(long bytes)
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

    internal static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }

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
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonOptions);
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
            .NotRequired()
            .MoreChoicesText("[grey](Move up/down for more)[/]")
            .InstructionsText("[grey]([blue]Space[/] to toggle, [green]Enter[/] to confirm, Enter with none to cancel)[/]")
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
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
        {
            var contains = lower.Trim('*');
            return episodes.Where(e =>
                e.FileName.ToLower().Contains(contains) ||
                e.Title.ToLower().Contains(contains)).ToList();
        }

        // Prefix pattern: text*
        if (pattern.EndsWith('*'))
        {
            var prefix = lower[..^1];
            return episodes.Where(e =>
                e.FileName.ToLower().StartsWith(prefix) ||
                e.Title.ToLower().StartsWith(prefix)).ToList();
        }

        // Suffix pattern: *text
        if (pattern.StartsWith('*'))
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

    private static async Task<bool> DownloadFFmpegWithProgressAsync()
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[cyan]Downloading FFmpeg...[/]", async _ => await FFmpegService.DownloadFFmpegAsync());
    }
}
