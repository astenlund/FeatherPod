using System.Reflection;
using FeatherPod.Commands;
using FeatherPod.Commands.Episode;
using Spectre.Console.Cli;

using ConfigCommands = FeatherPod.Commands.Config;
using FeedCommands = FeatherPod.Commands.Feed;
using PreferencesCommands = FeatherPod.Commands.Preferences;
using UserCommands = FeatherPod.Commands.User;

namespace FeatherPod;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.AddCommand<InteractiveCommand>("interactive")
                .WithDescription("Start interactive episode management")
                .IsHidden(); // Hidden since it's the default

            config.AddBranch("episode", episode =>
            {
                episode.SetDescription("Episode management commands");

                episode.AddCommand<ListCommand>("list")
                    .WithDescription("List episodes in a feed")
                    .WithExample("episode", "list", "-f", "my-podcast")
                    .WithExample("episode", "list");

                episode.AddCommand<PushCommand>("push")
                    .WithDescription("Upload episode(s) to the podcast feed")
                    .WithExample("episode", "push", "episode.mp3", "--title", "\"My Episode\"")
                    .WithExample("episode", "push", "*.mp3", "-x")
                    .WithExample("episode", "push", "ep1.mp3,ep2.mp3", "-e", "Test");

                episode.AddCommand<DeleteCommand>("delete")
                    .WithDescription("Delete an episode")
                    .WithExample("episode", "delete", "abc123", "-f", "my-podcast")
                    .WithExample("episode", "delete", "-f", "my-podcast");

                episode.AddCommand<MoveCommand>("move")
                    .WithDescription("Move episode(s) from one feed to another")
                    .WithExample("episode", "move")
                    .WithExample("episode", "move", "--from", "feed1", "--to", "feed2", "--episode", "abc123")
                    .WithExample("episode", "move", "--from", "feed1", "--to", "feed2", "--episode", "*");

                episode.AddCommand<CopyCommand>("copy")
                    .WithDescription("Copy episode(s) from one feed to another")
                    .WithExample("episode", "copy")
                    .WithExample("episode", "copy", "--from", "feed1", "--to", "feed2", "--episode", "Episode*");
            });

            config.AddBranch("user", user =>
            {
                user.SetDescription("User management commands (Admin only)");

                user.AddCommand<UserCommands.CreateCommand>("create")
                    .WithDescription("Create a new user")
                    .WithExample("user", "create", "john", "--name", "\"John Doe\"", "--email", "john@example.com", "--role", "Admin")
                    .WithExample("user", "create", "alice", "--role", "FeedOwner", "--feeds", "podcast1,podcast2");

                user.AddCommand<UserCommands.ListCommand>("list")
                    .WithDescription("List all users")
                    .WithExample("user", "list");

                user.AddCommand<UserCommands.DeleteCommand>("delete")
                    .WithDescription("Delete a user")
                    .WithExample("user", "delete", "john");

                user.AddCommand<UserCommands.GrantCommand>("grant")
                    .WithDescription("Grant feed ownership to a user")
                    .WithExample("user", "grant", "alice", "my-podcast");

                user.AddCommand<UserCommands.RevokeCommand>("revoke")
                    .WithDescription("Revoke feed ownership from a user")
                    .WithExample("user", "revoke", "alice", "my-podcast");

                user.AddCommand<UserCommands.RotateKeyCommand>("rotate-key")
                    .WithDescription("Regenerate a user's API key")
                    .WithExample("user", "rotate-key", "john");
            });

            config.AddBranch("feed", feed =>
            {
                feed.SetDescription("Feed management commands");

                feed.AddCommand<FeedCommands.ListCommand>("list")
                    .WithDescription("List all feeds")
                    .WithExample("feed", "list");

                feed.AddCommand<FeedCommands.CreateCommand>("create")
                    .WithDescription("Create a new feed")
                    .WithExample("feed", "create", "--id", "my-podcast", "--title", "\"My Podcast\"", "--author", "\"John Doe\"")
                    .WithExample("feed", "create");

                feed.AddCommand<FeedCommands.UpdateCommand>("update")
                    .WithDescription("Update feed metadata")
                    .WithExample("feed", "update", "my-podcast", "--title", "\"New Title\"")
                    .WithExample("feed", "update", "--description", "\"New description\"");

                feed.AddCommand<FeedCommands.RenameCommand>("rename")
                    .WithDescription("Rename a feed ID")
                    .WithExample("feed", "rename", "old-id", "new-id")
                    .WithExample("feed", "rename");

                feed.AddCommand<FeedCommands.DeleteCommand>("delete")
                    .WithDescription("Delete a feed and all its episodes")
                    .WithExample("feed", "delete", "my-podcast")
                    .WithExample("feed", "delete", "my-podcast", "--force");

                feed.AddCommand<FeedCommands.SetIconCommand>("set-icon")
                    .WithDescription("Upload/replace feed icon")
                    .WithExample("feed", "set-icon", "icon.png", "my-podcast")
                    .WithExample("feed", "set-icon", "artwork.jpg");

                feed.AddCommand<FeedCommands.UnsetIconCommand>("unset-icon")
                    .WithDescription("Remove feed icon")
                    .WithExample("feed", "unset-icon", "my-podcast")
                    .WithExample("feed", "unset-icon");

                feed.AddBranch("config", cfg =>
                {
                    cfg.SetDescription("Feed configuration commands");

                    cfg.AddCommand<FeedCommands.Config.ShowCommand>("show")
                        .WithDescription("Show feed configuration")
                        .WithExample("feed", "config", "show", "-f", "my-feed");

                    cfg.AddCommand<FeedCommands.Config.SetCommand>("set")
                        .WithDescription("Set feed configuration")
                        .WithExample("feed", "config", "set", "-f", "my-feed", "-x", "true");
                });
            });

            config.AddBranch("config", cfg =>
            {
                cfg.SetDescription("Configuration file commands");

                cfg.AddCommand<ConfigCommands.GenerateCommand>("generate")
                    .WithDescription("Generate configuration files from defaults")
                    .WithExample("config", "generate")
                    .WithExample("config", "generate", "--select");
            });

            config.AddBranch("preferences", prefs =>
            {
                prefs.SetDescription("User preferences commands");

                prefs.AddCommand<PreferencesCommands.ShowCommand>("show")
                    .WithDescription("Show all preferences")
                    .WithExample("preferences", "show")
                    .WithExample("preferences", "show", "-e", "Test");

                prefs.AddBranch("api-key", apiKey =>
                {
                    apiKey.SetDescription("API key preferences");

                    apiKey.AddCommand<PreferencesCommands.ApiKey.ShowCommand>("show")
                        .WithDescription("Show current API key")
                        .WithExample("preferences", "api-key", "show")
                        .WithExample("preferences", "api-key", "show", "-e", "Test");

                    apiKey.AddCommand<PreferencesCommands.ApiKey.SetCommand>("set")
                        .WithDescription("Set API key")
                        .WithExample("preferences", "api-key", "set", "<key>")
                        .WithExample("preferences", "api-key", "set", "<key>", "-e", "Test");
                });

                prefs.AddBranch("normalization", norm =>
                {
                    norm.SetDescription("Audio normalization preferences");

                    norm.AddCommand<PreferencesCommands.Normalization.ShowCommand>("show")
                        .WithDescription("Show normalization setting")
                        .WithExample("preferences", "normalization", "show");

                    norm.AddCommand<PreferencesCommands.Normalization.EnableCommand>("enable")
                        .WithDescription("Enable audio normalization")
                        .WithExample("preferences", "normalization", "enable");

                    norm.AddCommand<PreferencesCommands.Normalization.DisableCommand>("disable")
                        .WithDescription("Disable audio normalization")
                        .WithExample("preferences", "normalization", "disable");
                });

                prefs.AddBranch("auto-connect", autoConnect =>
                {
                    autoConnect.SetDescription("Auto-connect preferences");

                    autoConnect.AddCommand<PreferencesCommands.AutoConnect.ShowCommand>("show")
                        .WithDescription("Show auto-connect setting")
                        .WithExample("preferences", "auto-connect", "show");

                    autoConnect.AddCommand<PreferencesCommands.AutoConnect.EnableCommand>("enable")
                        .WithDescription("Enable auto-connect on startup")
                        .WithExample("preferences", "auto-connect", "enable");

                    autoConnect.AddCommand<PreferencesCommands.AutoConnect.DisableCommand>("disable")
                        .WithDescription("Disable auto-connect on startup")
                        .WithExample("preferences", "auto-connect", "disable");
                });
            })
            .WithAlias("prefs");

            // Version command
            config.AddCommand<VersionCommand>("version")
                .WithDescription("Show CLI and server version information")
                .WithExample("version")
                .WithExample("version", "-e", "Test");

            // Alias for backward compatibility
            config.AddCommand<PushCommand>("push")
                .WithDescription("Upload episode(s) to the podcast feed (alias for 'episode push')")
                .WithExample("push", "episode.mp3", "--title", "\"My Episode\"")
                .WithExample("push", "*.mp3", "-x")
                .WithExample("push", "ep1.mp3,ep2.mp3", "-e", "Test");

            config.SetApplicationName("FeatherPod");

            var versionAttribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();

            config.SetApplicationVersion(versionAttribute?.InformationalVersion ?? "0.1.0");
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
}
