using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Infrastructure;

internal static class PreferencesHelpers
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Override for the preferences directory. Used by tests to isolate preferences from the real AppData.
    /// </summary>
    internal static string? PreferencesDirectoryOverride { get; set; }

    /// <summary>
    /// Gets the path to the user preferences file in AppData.
    /// </summary>
    internal static string GetPreferencesPath()
    {
        var directory = PreferencesDirectoryOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherPod");

        return Path.Combine(directory, "preferences.json");
    }

    /// <summary>
    /// Gets whether admin features are enabled in the CLI.
    /// Returns null if not set (defaults to false — FeedOwner experience).
    /// </summary>
    internal static bool? GetEnableAdminFeatures()
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

            return root?["EnableAdminFeatures"]?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets whether admin features are enabled in the CLI.
    /// </summary>
    internal static void SetEnableAdminFeatures(bool enabled)
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

        root["EnableAdminFeatures"] = enabled;

        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
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
        Out.MarkupLine($"[yellow]No API key configured for {Markup.Escape(environment)} environment.[/]");
        Out.BlankLine();

        var apiKey = AnsiConsole.Prompt(new TextPrompt<string>("Enter your API key:").Secret().AllowEmpty());

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Out.Cancelled();

            return false;
        }

        SaveApiKey(environment, apiKey);

        var filePath = GetPreferencesPath();

        Out.Success($"API key saved to [cyan]{filePath}[/]");
        Out.BlankLine().Flush();

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
    /// Gets the audio normalization preference for the given environment.
    /// Returns null if not set (will use appsettings default).
    /// </summary>
    internal static bool? GetNormalizationEnabled(string environment)
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

            return root?["Environments"]?[environment]?["NormalizationEnabled"]?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the audio normalization preference for the given environment.
    /// </summary>
    internal static void SetNormalizationEnabled(string environment, bool enabled)
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

        environments[environment]!["NormalizationEnabled"] = enabled;

        // Write back with nice formatting
        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }

    /// <summary>
    /// Gets the auto-connect preference for the given environment.
    /// Returns null if not set (defaults to true - auto-connect enabled).
    /// </summary>
    internal static bool? GetAutoConnectEnabled(string environment)
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

            return root?["Environments"]?[environment]?["AutoConnectEnabled"]?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the auto-connect preference for the given environment.
    /// </summary>
    internal static void SetAutoConnectEnabled(string environment, bool enabled)
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

        environments[environment]!["AutoConnectEnabled"] = enabled;

        // Write back with nice formatting
        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }

    /// <summary>
    /// Gets the delete-after-upload trash preference for the given environment.
    /// Returns null if not set (defaults to true - use trash instead of permanent delete).
    /// </summary>
    internal static bool? GetDeleteAfterUploadUseTrash(string environment)
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

            return root?["Environments"]?[environment]?["DeleteAfterUploadUseTrash"]?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the delete-after-upload trash preference for the given environment.
    /// When true (default), files are sent to trash. When false, files are permanently deleted.
    /// </summary>
    internal static void SetDeleteAfterUploadUseTrash(string environment, bool useTrash)
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

        if (!root.ContainsKey("Environments"))
        {
            root["Environments"] = new JsonObject();
        }

        var environments = root["Environments"]!.AsObject();
        if (!environments.ContainsKey(environment))
        {
            environments[environment] = new JsonObject();
        }

        environments[environment]!["DeleteAfterUploadUseTrash"] = useTrash;

        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }

    /// <summary>
    /// Gets the last selected feed ID for the given environment.
    /// </summary>
    internal static string? GetLastSelectedFeed(string environment)
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

            return root?["Environments"]?[environment]?["LastSelectedFeed"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the last selected feed ID for the given environment.
    /// </summary>
    internal static void SetLastSelectedFeed(string environment, string? feedId)
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

        // Set or remove the feed ID
        if (feedId != null)
        {
            environments[environment]!["LastSelectedFeed"] = feedId;
        }
        else
        {
            environments[environment]!.AsObject().Remove("LastSelectedFeed");
        }

        // Write back with nice formatting
        File.WriteAllText(filePath, root.ToJsonString(JsonWriteOptions));
    }
}
