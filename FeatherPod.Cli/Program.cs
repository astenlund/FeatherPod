using FeatherPod.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

Console.WriteLine("FeatherPod Episode Manager");
Console.WriteLine();

try
{
    // Build configuration
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    Console.WriteLine($"Environment: {environment}");
    Console.WriteLine("Initializing...");

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    // Create logger factory
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Warning);
    });

    // Initialize services
    Console.Write("Connecting to blob storage... ");
    var blobStorageLogger = loggerFactory.CreateLogger<BlobStorageService>();
    var blobStorage = new BlobStorageService(configuration, blobStorageLogger);
    await blobStorage.InitializeAsync();
    Console.WriteLine("OK");

    Console.Write("Loading episodes... ");
    var episodeServiceLogger = loggerFactory.CreateLogger<EpisodeService>();
    var episodeService = new EpisodeService(blobStorage, configuration, episodeServiceLogger);
    await episodeService.InitializeAsync();
    Console.WriteLine("OK");
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
            await ListEpisodesAsync(episodeService);
            break;

        case "2":
        case "delete":
            await DeleteEpisodeAsync(episodeService);
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
    Console.WriteLine("  - Make sure Azurite is running if using Development environment");
    Console.WriteLine("    Start with: azurite --silent --location $env:USERPROFILE\\.azurite");
    Console.WriteLine("  - Check your connection string in appsettings.json");
    Console.WriteLine("  - Verify Azure credentials if using production settings");
    Console.WriteLine();
    Console.WriteLine("Full error details:");
    Console.WriteLine(ex.ToString());
    return;
}

static async Task ListEpisodesAsync(EpisodeService episodeService)
{
    var episodes = await episodeService.GetAllEpisodesAsync();

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

static async Task DeleteEpisodeAsync(EpisodeService episodeService)
{
    var episodes = await episodeService.GetAllEpisodesAsync();

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

    var success = await episodeService.DeleteEpisodeAsync(episodeToDelete.Id);

    if (success)
    {
        Console.WriteLine($"Deleted: {episodeToDelete.Title}");
    }
    else
    {
        Console.WriteLine("Failed to delete episode.");
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
