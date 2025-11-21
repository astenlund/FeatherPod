using System.Reflection;
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

    internal static IConfiguration BuildConfiguration(string environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory);

        // 1. Load embedded resources first (defaults)
        var assembly = Assembly.GetExecutingAssembly();
        AddEmbeddedJsonConfig(builder, assembly, "FeatherPod.appsettings.json");
        AddEmbeddedJsonConfig(builder, assembly, $"FeatherPod.appsettings.{environment}.json");

        // 2. Physical files override embedded (optional)
        builder.AddJsonFile("appsettings.json", optional: true);
        builder.AddJsonFile($"appsettings.{environment}.json", optional: true);

        // 3. Environment variables override all
        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    private static void AddEmbeddedJsonConfig(IConfigurationBuilder builder, Assembly assembly, string resourceName)
    {
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            builder.AddJsonStream(stream);
        }
    }

    internal static async Task<(HttpClient?, IConfiguration?)> SetupHttpClientAsync(string environment)
    {
        var configuration = BuildConfiguration(environment);

        var apiBaseUrl = configuration["Api:BaseUrl"]
            ?? throw new InvalidOperationException("Api:BaseUrl not configured in appsettings.json");

        // Get API key from preferences (with auto-migration from legacy .Local.json)
        var apiKey = PreferencesHelpers.GetApiKey(environment);

        if (string.IsNullOrEmpty(apiKey))
        {
            // Prompt user to enter API key
            if (!PreferencesHelpers.PromptAndSaveApiKey(environment))
            {
                return (null, null);
            }

            // Get the newly saved API key
            apiKey = PreferencesHelpers.GetApiKey(environment);
            if (string.IsNullOrEmpty(apiKey))
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to load API key after saving.");
                return (null, null);
            }
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
