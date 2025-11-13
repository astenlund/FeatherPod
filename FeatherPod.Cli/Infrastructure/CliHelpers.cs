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

    internal static string? GetEnvironment(string? environment)
    {
        if (string.IsNullOrEmpty(environment))
        {
            var environments = new Dictionary<string, string>
            {
                ["Dev"] = "Dev - Local (localhost:8080 with Azurite)",
                ["Test"] = "Test - featherpod-test.azurewebsites.net",
                ["Prod"] = "Prod - featherpod.azurewebsites.net"
            };

            environment = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [green]environment[/]:")
                    .AddChoices(environments.Keys)
                    .UseConverter(key => environments[key]));
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

        AnsiConsole.MarkupLine($"API: [cyan]{apiBaseUrl}[/]");
        AnsiConsole.WriteLine();

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
                    var response = await httpClient.GetAsync("/api/episodes");
                    response.EnsureSuccessStatusCode();
                });

            AnsiConsole.MarkupLine("[green]✓[/] Connected");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Connection failed");
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Make sure the FeatherPod API is running and accessible.");
            return (null, null);
        }

        AnsiConsole.WriteLine();

        return (httpClient, configuration);
    }

    internal static async Task ListEpisodesAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/episodes");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonSerializerOptions) ?? [];

            if (episodes.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No episodes found.[/]");
                return;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("Published");
            table.AddColumn("Title");
            table.AddColumn("File");
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
                    episode.Title,
                    $"[cyan]{episode.FileName}[/]",
                    formattedSize,
                    formattedDuration
                );
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]Total: {episodes.Count} episodes[/]");
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching episodes:[/] {ex.Message}");
        }
    }

    internal static async Task DeleteEpisodeAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/episodes");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonSerializerOptions) ?? [];

            if (episodes.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No episodes to delete.[/]");
                return;
            }

            var choices = episodes.Select((ep, i) =>
                $"{i + 1}. [{ep.PublishedDate:yyyy-MM-dd}] {ep.Title} ({ep.FileName})").ToList();
            choices.Add("[grey]← Cancel[/]");

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select episode to [red]delete[/]:")
                    .PageSize(10)
                    .AddChoices(choices));

            if (selection.Contains("Cancel"))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return;
            }

            var index = int.Parse(selection.Split('.')[0]) - 1;
            var episodeToDelete = episodes[index];

            if (!await AnsiConsole.ConfirmAsync($"Delete [red]{episodeToDelete.Title}[/] ({episodeToDelete.FileName})?"))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return;
            }

            var deleteResponse = await httpClient.DeleteAsync($"/api/episodes/{episodeToDelete.Id}");

            if (deleteResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted: {episodeToDelete.Title}");
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

    internal static async Task<bool> UploadEpisodeAsync(HttpClient httpClient, string filePath, PushSettings settings)
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
                    else if (settings.ExtractDateFromFile)
                    {
                        content.Add(new StringContent("true"), "useMetadataForPublishedDate");
                    }
                    // Note: UseCurrentDate is the default behavior, no need to send anything

                    // Upload
                    var response = await httpClient.PostAsync("/api/episodes", content);

                    if (response.IsSuccessStatusCode)
                    {
                        success = true;
                        var responseContent = await response.Content.ReadAsStringAsync();
                        var episode = JsonSerializer.Deserialize<Episode>(responseContent, JsonSerializerOptions);

                        AnsiConsole.MarkupLine($"[green]✓[/] Uploaded: [cyan]{fileName}[/]");
                        if (episode != null)
                        {
                            AnsiConsole.MarkupLine($"  ID: [grey]{episode.Id}[/]");
                            AnsiConsole.MarkupLine($"  Title: {episode.Title}");
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
