using FeatherPod.Models;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FeatherPod.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.AddCommand<InteractiveCommand>("interactive")
                .WithDescription("Start interactive episode management")
                .IsHidden(); // Hidden since it's the default

            config.SetApplicationName("featherpod-cli");
        });

        // If no command specified, run interactive mode
        if (args.Length == 0 || (args.Length >= 1 && (args[0] == "-e" || args[0] == "--environment")))
        {
            var newArgs = new List<string> { "interactive" };
            newArgs.AddRange(args);
            return await app.RunAsync(newArgs.ToArray());
        }

        return await app.RunAsync(args);
    }

    // ============================================================================
    // Helper Functions
    // ============================================================================

    internal static async Task<string?> GetEnvironmentAsync(string? environment)
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
                .StartAsync("Testing API connection...", async ctx =>
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
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<Episode>();

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

            for (int i = 0; i < episodes.Count; i++)
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
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<Episode>();

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

            if (!AnsiConsole.Confirm($"Delete [red]{episodeToDelete.Title}[/] ({episodeToDelete.FileName})?"))
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

    internal static string FormatFileSize(long bytes)
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

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        else
            return $"{duration.Minutes}:{duration.Seconds:D2}";
    }
}

// ============================================================================
// Commands
// ============================================================================

sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
        AnsiConsole.WriteLine();

        var env = await Program.GetEnvironmentAsync(settings.Environment);
        if (env == null) return 1;

        var (httpClient, config) = await Program.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Main menu loop
        while (true)
        {
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .AddChoices(new[] {
                        "List episodes",
                        "Delete episode",
                        "Exit"
                    }));

            switch (choice)
            {
                case "List episodes":
                    await Program.ListEpisodesAsync(httpClient);
                    break;

                case "Delete episode":
                    await Program.DeleteEpisodeAsync(httpClient);
                    break;

                case "Exit":
                    AnsiConsole.MarkupLine("[grey]Bye.[/]");
                    return 0;
            }
        }
    }
}

// ============================================================================
// Settings Classes
// ============================================================================

sealed class InteractiveSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
