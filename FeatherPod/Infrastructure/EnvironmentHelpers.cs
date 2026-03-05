using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

using static System.Net.HttpStatusCode;
using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Infrastructure;

internal record CurrentUserInfo(string Id, string Role, List<string> OwnedFeeds)
{
    internal bool CanAccessFeed(string feedId)
    {
        return Role == "Admin" || (Role == "FeedOwner" && OwnedFeeds.Contains(feedId));
    }
}

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
                if (environment == null)
                {
                    return null;
                }
            }
        }

        if (environment != "Dev" && environment != "Test" && environment != "Prod")
        {
            Out.Error($"Invalid environment: {environment}");
            Out.MarkupLine("Valid options: Dev, Test, Prod");

            return null;
        }

        Out.MarkupLine($"Environment: [cyan]{environment}[/]");
        Out.BlankLine();

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
        var builder = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory);

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

    internal static async Task<(HttpClient?, CurrentUserInfo?)> SetupHttpClientAsync(string environment)
    {
        var configuration = BuildConfiguration(environment);

        var apiBaseUrl = configuration["Api:BaseUrl"] ?? throw new InvalidOperationException("Api:BaseUrl not configured in appsettings.json");

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
                Out.Error("Failed to load API key after saving.");

                return (null, null);
            }
        }

        Out.MarkupLine($"API: [cyan]{apiBaseUrl}/api[/]");

        var httpClient = new HttpClient
        {
            BaseAddress = new(apiBaseUrl)
        };
        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        CurrentUserInfo? userInfo = null;

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Testing API connection...", async _ =>
                {
                    // Use /api/users/me to verify both connectivity and authentication
                    var response = await httpClient.GetAsync("/api/users/me");
                    response.EnsureSuccessStatusCode();

                    // Parse user info from response
                    var json = await response.Content.ReadAsStringAsync();
                    var userData = JsonSerializer.Deserialize<JsonElement>(json);

                    var id = userData.GetProperty("id").GetString() ?? "";
                    var role = userData.GetProperty("role").GetString() ?? "FeedOwner";
                    var ownedFeeds = new List<string>();

                    if (userData.TryGetProperty("ownedFeeds", out var feedsElement) && feedsElement.ValueKind == JsonValueKind.Array)
                    {
                        ownedFeeds = feedsElement.EnumerateArray()
                            .Select(e => e.GetString() ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                    }

                    userInfo = new(id, role, ownedFeeds);
                });

            Out.BlankLine();
            Out.Success("Connected");
            Out.BlankLine();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == Unauthorized)
        {
            Out.BlankLine();
            Out.Error("Authentication failed: invalid API key.");
            Out.BlankLine().Flush();

            return (null, null);
        }
        catch (Exception ex)
        {
            Out.BlankLine();
            Out.Error($"Connection failed: {ex.Message}");
            Out.BlankLine();
            Out.MarkupLine("Make sure the FeatherPod server is running and accessible.");
            Out.BlankLine().Flush();

            return (null, null);
        }

        return (httpClient, userInfo);
    }
}
