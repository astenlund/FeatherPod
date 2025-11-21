using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class ApiKeyHelpers
{
    /// <summary>
    /// Gets the path to the user preferences file in AppData.
    /// </summary>
    internal static string GetPreferencesPath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "FeatherPod", "preferences.json");
    }

    /// <summary>
    /// Gets the path to the legacy local settings file (for migration).
    /// </summary>
    internal static string GetLegacyLocalSettingsPath(string environment)
    {
        return Path.Combine(AppContext.BaseDirectory, $"appsettings.{environment}.Local.json");
    }

    /// <summary>
    /// Saves the API key to the user preferences file in AppData.
    /// Creates the file if it doesn't exist, preserves other settings if it does.
    /// </summary>
    internal static void SaveApiKey(string environment, string apiKey)
    {
        var filePath = GetPreferencesPath();
        var directory = Path.GetDirectoryName(filePath)!;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

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

        // Ensure Environments section exists
        if (!root.ContainsKey("Environments"))
        {
            root["Environments"] = new JsonObject();
        }

        // Ensure environment section exists
        var environments = root["Environments"]!.AsObject();
        if (!environments.ContainsKey(environment))
        {
            environments[environment] = new JsonObject();
        }

        // Set the API key
        environments[environment]!["ApiKey"] = apiKey;

        // Write back with nice formatting
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(filePath, root.ToJsonString(options));
    }

    /// <summary>
    /// Gets the current API key from user preferences, with migration from legacy .Local.json files.
    /// </summary>
    internal static string? GetApiKey(string environment)
    {
        // First check AppData preferences
        var preferencesPath = GetPreferencesPath();
        if (File.Exists(preferencesPath))
        {
            try
            {
                var content = File.ReadAllText(preferencesPath);
                var root = JsonNode.Parse(content);
                var apiKey = root?["Environments"]?[environment]?["ApiKey"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(apiKey))
                {
                    return apiKey;
                }
            }
            catch
            {
                // Fall through to legacy check
            }
        }

        // Check legacy .Local.json and migrate if found
        var legacyPath = GetLegacyLocalSettingsPath(environment);
        if (File.Exists(legacyPath))
        {
            try
            {
                var content = File.ReadAllText(legacyPath);
                var root = JsonNode.Parse(content);
                var apiKey = root?["Api"]?["ApiKey"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(apiKey))
                {
                    // Auto-migrate to AppData
                    SaveApiKey(environment, apiKey);
                    return apiKey;
                }
            }
            catch
            {
                // Ignore errors reading legacy file
            }
        }

        return null;
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

        var filePath = GetPreferencesPath();

        AnsiConsole.MarkupLine($"[green]✓[/] API key saved to [cyan]{filePath}[/]");
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
