using FeatherPod.Models;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

Console.WriteLine("FeatherPod Episode Manager");
Console.WriteLine();

try
{
    // Parse command line arguments for environment
    string? selectedEnvironment = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--environment" || args[i] == "--env" || args[i] == "-e")
        {
            if (i + 1 < args.Length)
            {
                selectedEnvironment = args[i + 1];
                break;
            }
        }
    }

    // If no environment specified, show interactive menu
    if (string.IsNullOrEmpty(selectedEnvironment))
    {
        selectedEnvironment = ShowEnvironmentMenu();
    }

    // Validate environment
    if (selectedEnvironment != "Development" && selectedEnvironment != "Test" && selectedEnvironment != "Prod")
    {
        Console.WriteLine($"Invalid environment: {selectedEnvironment}");
        Console.WriteLine("Valid options: Development, Test, Prod");
        return;
    }

    Console.WriteLine($"Environment: {selectedEnvironment}");
    Console.WriteLine();

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{selectedEnvironment}.json", optional: true)
        .AddJsonFile($"appsettings.{selectedEnvironment}.Local.json", optional: true) // Local overrides (not in git)
        .AddEnvironmentVariables()
        .Build();

    var apiBaseUrl = configuration["Api:BaseUrl"]
        ?? throw new InvalidOperationException("Api:BaseUrl not configured in appsettings.json");

    // API key from local file or environment variable (both not in source control)
    var apiKey = configuration["Api:ApiKey"];

    if (string.IsNullOrEmpty(apiKey))
    {
        Console.WriteLine("ERROR: API key not configured.");
        Console.WriteLine();
        Console.WriteLine("Option 1 (Recommended): Create a local settings file:");
        Console.WriteLine($"  File: appsettings.{selectedEnvironment}.Local.json");
        Console.WriteLine("  Content: { \"Api\": { \"ApiKey\": \"your-api-key-here\" } }");
        Console.WriteLine();
        Console.WriteLine("Option 2: Set environment variable:");
        Console.WriteLine("  $env:Api__ApiKey = \"your-api-key-here\"  (PowerShell)");
        Console.WriteLine("  export Api__ApiKey=\"your-api-key-here\"  (Bash)");
        return;
    }

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

static string ShowEnvironmentMenu()
{
    var environments = new[] { "Development", "Test", "Prod" };
    var descriptions = new[]
    {
        "Local development (localhost:8080 with Azurite)",
        "Test environment (featherpod-test.azurewebsites.net)",
        "Production (featherpod.azurewebsites.net)"
    };

    int selectedIndex = 0;

    Console.WriteLine("Select environment:");
    Console.WriteLine();

    while (true)
    {
        // Display menu
        for (int i = 0; i < environments.Length; i++)
        {
            if (i == selectedIndex)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(" > ");
            }
            else
            {
                Console.Write("   ");
            }

            Console.Write($"{i + 1}. {environments[i]}");
            Console.ResetColor();
            Console.WriteLine($" - {descriptions[i]}");
        }

        Console.WriteLine();
        Console.Write("Select (1-3, arrows, or Enter): ");

        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        // Handle number keys
        if (key.KeyChar >= '1' && key.KeyChar <= '3')
        {
            selectedIndex = key.KeyChar - '1';

            // Wait for Enter or accept immediately
            var nextKey = Console.ReadKey(intercept: true);
            if (nextKey.Key == ConsoleKey.Enter || nextKey.KeyChar == '\r' || nextKey.KeyChar == '\n')
            {
                Console.WriteLine();
                return environments[selectedIndex];
            }
            else
            {
                Console.WriteLine();
                return environments[selectedIndex];
            }
        }

        // Handle arrow keys
        if (key.Key == ConsoleKey.UpArrow)
        {
            selectedIndex = (selectedIndex - 1 + environments.Length) % environments.Length;
        }
        else if (key.Key == ConsoleKey.DownArrow)
        {
            selectedIndex = (selectedIndex + 1) % environments.Length;
        }
        else if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return environments[selectedIndex];
        }

        // Clear previous menu
        Console.SetCursorPosition(0, Console.CursorTop - environments.Length - 2);
        for (int i = 0; i < environments.Length + 2; i++)
        {
            Console.Write(new string(' ', Console.WindowWidth - 1));
            Console.WriteLine();
        }
        Console.SetCursorPosition(0, Console.CursorTop - environments.Length - 2);
    }
}
