using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class ApiKeyHelpers
{
    /// <summary>
    /// Gets the path to the local settings file for the specified environment.
    /// </summary>
    internal static string GetLocalSettingsPath(string environment)
    {
        return Path.Combine(AppContext.BaseDirectory, $"appsettings.{environment}.Local.json");
    }

    /// <summary>
    /// Saves the API key to the local settings file for the specified environment.
    /// Creates the file if it doesn't exist, preserves other settings if it does.
    /// </summary>
    internal static void SaveApiKey(string environment, string apiKey)
    {
        var filePath = GetLocalSettingsPath(environment);
        JsonObject root;

        if (File.Exists(filePath))
        {
            var existingContent = File.ReadAllText(filePath);
            root = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new();
        }

        // Ensure Api section exists
        if (!root.ContainsKey("Api"))
        {
            root["Api"] = new JsonObject();
        }

        // Set the API key
        root["Api"]!["ApiKey"] = apiKey;

        // Write back with nice formatting
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(filePath, root.ToJsonString(options));
    }

    /// <summary>
    /// Gets the current API key from the local settings file, or null if not configured.
    /// </summary>
    internal static string? GetApiKey(string environment)
    {
        var filePath = GetLocalSettingsPath(environment);

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(filePath);
            var root = JsonNode.Parse(content);

            return root?["Api"]?["ApiKey"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prompts the user to enter their API key and saves it.
    /// Returns true if successful, false if cancelled.
    /// </summary>
    internal static bool PromptAndSaveApiKey(string environment)
    {
        AnsiConsole.MarkupLine($"[yellow]No API key configured for {environment} environment.[/]");
        AnsiConsole.WriteLine();

        var apiKey = AnsiConsole.Prompt(new TextPrompt<string>("Enter your API key:").Secret().AllowEmpty());

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

            return false;
        }

        SaveApiKey(environment, apiKey);

        var filePath = GetLocalSettingsPath(environment);

        AnsiConsole.MarkupLine($"[green]✓[/] API key saved to [cyan]{Path.GetFileName(filePath)}[/]");
        AnsiConsole.WriteLine();

        return true;
    }

    /// <summary>
    /// Masks an API key for display, showing only first 4 and last 4 characters.
    /// </summary>
    internal static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return "(not set)";
        }

        return apiKey.Length <= 8
            ? new('*', apiKey.Length)
            : $"{apiKey[..4]}{"*".PadRight(apiKey.Length - 8, '*')}{apiKey[^4..]}";
    }
}
