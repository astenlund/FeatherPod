using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class PreferencesHelpers
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };
    /// <summary>
    /// Gets the path to the user preferences file in AppData.
    /// </summary>
    internal static string GetPreferencesPath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "FeatherPod", "preferences.json");
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
        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }

    /// <summary>
    /// Gets the current API key from user preferences.
    /// </summary>
    internal static string? GetApiKey(string environment)
    {
        var preferencesPath = GetPreferencesPath();
        if (!File.Exists(preferencesPath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(preferencesPath);
            var root = JsonNode.Parse(content);

            return root?["Environments"]?[environment]?["ApiKey"]?.GetValue<string>();
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

    /// <summary>
    /// Gets the audio normalization preference from user preferences.
    /// Returns null if not set (will use appsettings default).
    /// </summary>
    internal static bool? GetNormalizationEnabled()
    {
        var preferencesPath = GetPreferencesPath();
        if (!File.Exists(preferencesPath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(preferencesPath);
            var root = JsonNode.Parse(content);
            var enabled = root?["AudioNormalization"]?["Enabled"];

            return enabled?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the audio normalization preference in user preferences.
    /// </summary>
    internal static void SetNormalizationEnabled(bool enabled)
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

        // Ensure AudioNormalization section exists
        if (!root.ContainsKey("AudioNormalization"))
        {
            root["AudioNormalization"] = new JsonObject();
        }

        root["AudioNormalization"]!["Enabled"] = enabled;

        // Write back with nice formatting
        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }

    /// <summary>
    /// Gets the auto-connect preference from user preferences.
    /// Returns null if not set (defaults to true - auto-connect enabled).
    /// </summary>
    internal static bool? GetAutoConnectEnabled()
    {
        var preferencesPath = GetPreferencesPath();
        if (!File.Exists(preferencesPath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(preferencesPath);
            var root = JsonNode.Parse(content);
            var enabled = root?["AutoConnect"]?["Enabled"];

            return enabled?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the auto-connect preference in user preferences.
    /// </summary>
    internal static void SetAutoConnectEnabled(bool enabled)
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

        // Ensure AutoConnect section exists
        if (!root.ContainsKey("AutoConnect"))
        {
            root["AutoConnect"] = new JsonObject();
        }

        root["AutoConnect"]!["Enabled"] = enabled;

        // Write back with nice formatting
        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }
}
