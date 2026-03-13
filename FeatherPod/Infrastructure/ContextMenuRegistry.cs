using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FeatherPod.Infrastructure;

internal record ContextMenuEntry(string FeedId, string FeedTitle, string Environment, string BridgePath);

[SupportedOSPlatform("windows")]
internal static class ContextMenuRegistry
{
    private const string DefaultRegistryKeyPrefix = @"Software\Classes\SystemFileAssociations";
    private const string KeyPrefix = "FeatherPod.";

    internal static void Install(string feedId, string feedTitle, string bridgePath, string cliPath, string environment, string registryKeyPrefix = DefaultRegistryKeyPrefix)
    {
        var shellKeyName = $"{KeyPrefix}{feedId}";
        var displayName = $"Push to {feedTitle}";
        var command = $"\"{bridgePath}\" push --headless --feed {feedId} --environment {environment} \"%1\"";

        foreach (var ext in AudioExtensions.All)
        {
            var shellKeyPath = $@"{registryKeyPrefix}\{ext}\shell\{shellKeyName}";

            using var shellKey = Registry.CurrentUser.CreateSubKey(shellKeyPath);
            shellKey.SetValue(null, displayName);
            shellKey.SetValue("Icon", cliPath);

            using var commandKey = Registry.CurrentUser.CreateSubKey($@"{shellKeyPath}\command");
            commandKey.SetValue(null, command);
        }
    }

    internal static List<ContextMenuEntry> GetInstalled(string registryKeyPrefix = DefaultRegistryKeyPrefix)
    {
        var entries = new Dictionary<string, ContextMenuEntry>();

        foreach (var ext in AudioExtensions.All)
        {
            var shellPath = $@"{registryKeyPrefix}\{ext}\shell";

            using var shellKey = Registry.CurrentUser.OpenSubKey(shellPath);
            if (shellKey is null)
            {
                continue;
            }

            foreach (var subKeyName in shellKey.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var feedId = subKeyName[KeyPrefix.Length..];
                if (entries.ContainsKey(feedId))
                {
                    continue;
                }

                using var entryKey = shellKey.OpenSubKey(subKeyName);
                if (entryKey is null)
                {
                    continue;
                }

                var displayName = entryKey.GetValue(null) as string ?? "";
                var feedTitle = displayName.StartsWith("Push to ", StringComparison.Ordinal)
                    ? displayName["Push to ".Length..]
                    : displayName;

                var bridgePath = "";
                var environment = "Prod";

                using var commandKey = entryKey.OpenSubKey("command");
                if (commandKey?.GetValue(null) is string commandValue)
                {
                    bridgePath = ParseBridgePath(commandValue);
                    environment = ParseEnvironment(commandValue);
                }

                entries[feedId] = new ContextMenuEntry(feedId, feedTitle, environment, bridgePath);
            }
        }

        return entries.Values.ToList();
    }

    internal static void Remove(string feedId, string registryKeyPrefix = DefaultRegistryKeyPrefix)
    {
        var shellKeyName = $"{KeyPrefix}{feedId}";

        foreach (var ext in AudioExtensions.All)
        {
            var shellPath = $@"{registryKeyPrefix}\{ext}\shell";

            using var shellKey = Registry.CurrentUser.OpenSubKey(shellPath, writable: true);
            if (shellKey is null)
            {
                continue;
            }

            shellKey.DeleteSubKeyTree(shellKeyName, throwOnMissingSubKey: false);
        }
    }

    internal static void RemoveAll(string registryKeyPrefix = DefaultRegistryKeyPrefix)
    {
        foreach (var ext in AudioExtensions.All)
        {
            var shellPath = $@"{registryKeyPrefix}\{ext}\shell";

            using var shellKey = Registry.CurrentUser.OpenSubKey(shellPath, writable: true);
            if (shellKey is null)
            {
                continue;
            }

            foreach (var subKeyName in shellKey.GetSubKeyNames())
            {
                if (subKeyName.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    shellKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                }
            }
        }
    }

    private static string ParseBridgePath(string commandValue)
    {
        if (commandValue.StartsWith('"'))
        {
            var endQuote = commandValue.IndexOf('"', 1);
            if (endQuote > 0)
            {
                return commandValue[1..endQuote];
            }
        }

        return "";
    }

    private static string ParseEnvironment(string commandValue)
    {
        var envFlag = "--environment ";
        var index = commandValue.IndexOf(envFlag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "Prod";
        }

        var start = index + envFlag.Length;
        var end = commandValue.IndexOf(' ', start);

        return end < 0 ? commandValue[start..] : commandValue[start..end];
    }
}
