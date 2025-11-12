using FeatherPod.Models;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

Console.WriteLine("FeatherPod Episode Manager");
Console.WriteLine();

try
{
    // Build configuration
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    Console.WriteLine($"Environment: {environment}");

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var apiBaseUrl = configuration["Api:BaseUrl"]
        ?? throw new InvalidOperationException("Api:BaseUrl not configured in appsettings.json");

    // API key must come from environment variable (not checked into source control)
    var apiKey = configuration["Api:ApiKey"]
        ?? throw new InvalidOperationException(
            "Api:ApiKey not set. Please set environment variable:\n" +
            "  $env:Api__ApiKey = \"your-api-key-here\"  (PowerShell)\n" +
            "  export Api__ApiKey=\"your-api-key-here\"  (Bash)");

    Console.WriteLine($"API: {apiBaseUrl}");
    Console.WriteLine();

    // Create HTTP client
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(apiBaseUrl)
    };
    httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

    // Test connection
    Console.Write("Testing API connection... ");
    try
    {
        var response = await httpClient.GetAsync("/api/episodes");
        response.EnsureSuccessStatusCode();
        Console.WriteLine("OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAILED");
        Console.WriteLine($"Error: {ex.Message}");
        Console.WriteLine();
        Console.WriteLine("Make sure the FeatherPod API is running and accessible.");
        return;
    }

    Console.WriteLine();

    // Main menu loop
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("*** Commands ***");
        Console.WriteLine("  1: list        - list all episodes");
        Console.WriteLine("  2: delete      - delete an episode");
        Console.WriteLine("  q: quit        - exit");
        Console.WriteLine();
        Console.Write("What now> ");

        var input = Console.ReadLine()?.Trim().ToLower();

        if (string.IsNullOrEmpty(input))
            continue;

        switch (input)
        {
            case "1":
            case "list":
                await ListEpisodesAsync(httpClient);
                break;

            case "2":
            case "delete":
                await DeleteEpisodeAsync(httpClient);
                break;

            case "q":
            case "quit":
                Console.WriteLine("Bye.");
                return;

            default:
                Console.WriteLine($"Unknown command: {input}");
                break;
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"ERROR: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Troubleshooting:");
    Console.WriteLine("  - Check your API configuration in appsettings.json");
    Console.WriteLine("  - Make sure the FeatherPod API is running");
    Console.WriteLine("  - Verify the API key matches the server configuration");
    Console.WriteLine();
    Console.WriteLine("Full error details:");
    Console.WriteLine(ex.ToString());
    return;
}

static async Task ListEpisodesAsync(HttpClient httpClient)
{
    try
    {
        var response = await httpClient.GetAsync("/api/episodes");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var episodes = JsonSerializer.Deserialize<List<Episode>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<Episode>();

        if (episodes.Count == 0)
        {
            Console.WriteLine("No episodes found.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Episodes ({episodes.Count} total):");
        Console.WriteLine();

        for (int i = 0; i < episodes.Count; i++)
        {
            var episode = episodes[i];
            var formattedDate = episode.PublishedDate.ToString("yyyy-MM-dd HH:mm");
            var formattedSize = FormatFileSize(episode.FileSize);
            var formattedDuration = FormatDuration(episode.Duration);

            Console.WriteLine($"  {i + 1,3}. [{formattedDate}] {episode.Title}");
            Console.WriteLine($"       File: {episode.FileName} ({formattedSize}, {formattedDuration})");
            Console.WriteLine($"       ID: {episode.Id}");

            if (!string.IsNullOrEmpty(episode.Description))
            {
                var shortDesc = episode.Description.Length > 80
                    ? episode.Description.Substring(0, 77) + "..."
                    : episode.Description;
                Console.WriteLine($"       Desc: {shortDesc}");
            }

            Console.WriteLine();
        }
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Error fetching episodes: {ex.Message}");
    }
}

static async Task DeleteEpisodeAsync(HttpClient httpClient)
{
    try
    {
        // Fetch episodes
        var response = await httpClient.GetAsync("/api/episodes");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var episodes = JsonSerializer.Deserialize<List<Episode>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<Episode>();

        if (episodes.Count == 0)
        {
            Console.WriteLine("No episodes to delete.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Select episodes to delete:");
        Console.WriteLine();

        for (int i = 0; i < episodes.Count; i++)
        {
            var episode = episodes[i];
            var formattedDate = episode.PublishedDate.ToString("yyyy-MM-dd");
            Console.WriteLine($"  {i + 1,3}. [{formattedDate}] {episode.Title} ({episode.FileName})");
        }

        Console.WriteLine();
        Console.Write("Delete episode (number or 'q' to cancel)> ");

        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) || input.ToLower() == "q")
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        if (!int.TryParse(input, out int selection) || selection < 1 || selection > episodes.Count)
        {
            Console.WriteLine($"Invalid selection: {input}");
            return;
        }

        var episodeToDelete = episodes[selection - 1];

        Console.WriteLine();
        Console.WriteLine($"Delete episode: {episodeToDelete.Title} ({episodeToDelete.FileName})");
        Console.Write("Are you sure? (y/N)> ");

        var confirm = Console.ReadLine()?.Trim().ToLower();

        if (confirm != "y" && confirm != "yes")
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        // Delete via API
        var deleteResponse = await httpClient.DeleteAsync($"/api/episodes/{episodeToDelete.Id}");

        if (deleteResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"Deleted: {episodeToDelete.Title}");
        }
        else if (deleteResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine("Episode not found (may have already been deleted).");
        }
        else if (deleteResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Console.WriteLine("Unauthorized: Check your API key configuration.");
        }
        else
        {
            Console.WriteLine($"Failed to delete episode: {deleteResponse.StatusCode}");
            var errorContent = await deleteResponse.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(errorContent))
            {
                Console.WriteLine($"Error: {errorContent}");
            }
        }
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Error deleting episode: {ex.Message}");
    }
}

static string FormatFileSize(long bytes)
{
    string[] sizes = { "B", "KB", "MB", "GB" };
    double len = bytes;
    int order = 0;

    while (len >= 1024 && order < sizes.Length - 1)
    {
        order++;
        len /= 1024;
    }

    return $"{len:0.##} {sizes[order]}";
}

static string FormatDuration(TimeSpan duration)
{
    if (duration.TotalHours >= 1)
        return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    else
        return $"{duration.Minutes}:{duration.Seconds:D2}";
}
