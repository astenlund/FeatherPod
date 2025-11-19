using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class EnvironmentHelpers
{
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
}
