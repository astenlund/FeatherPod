using FeatherPod.Commands;
using FeatherPod.Commands.Episode;
using FeatherPod.Commands.Icon;
using FeatherPod.Commands.User;
using Spectre.Console.Cli;

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

                episode.AddCommand<PushCommand>("push")
                    .WithDescription("Upload episode(s) to the podcast feed")
                    .WithExample("episode", "push", "episode.mp3", "--title", "\"My Episode\"")
                    .WithExample("episode", "push", "*.mp3", "-x")
                    .WithExample("episode", "push", "ep1.mp3,ep2.mp3", "-e", "Test");

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

            config.AddBranch("icon", icon =>
            {
                icon.SetDescription("Icon management commands");

                icon.AddCommand<SetCommand>("set")
                    .WithDescription("Upload/replace feed icon")
                    .WithExample("icon", "set", "icon.png", "--feed", "my-podcast")
                    .WithExample("icon", "set", "artwork.jpg");

                icon.AddCommand<UnsetCommand>("unset")
                    .WithDescription("Remove feed icon")
                    .WithExample("icon", "unset", "--feed", "my-podcast")
                    .WithExample("icon", "unset");
            });

            config.AddBranch("user", user =>
            {
                user.SetDescription("User management commands (Admin only)");

                user.AddCommand<CreateCommand>("create")
                    .WithDescription("Create a new user")
                    .WithExample("user", "create", "john", "--name", "\"John Doe\"", "--email", "john@example.com", "--role", "Admin")
                    .WithExample("user", "create", "alice", "--role", "FeedOwner", "--feeds", "podcast1,podcast2");

                user.AddCommand<ListCommand>("list")
                    .WithDescription("List all users")
                    .WithExample("user", "list");

                user.AddCommand<DeleteCommand>("delete")
                    .WithDescription("Delete a user")
                    .WithExample("user", "delete", "john");

                user.AddCommand<GrantCommand>("grant")
                    .WithDescription("Grant feed ownership to a user")
                    .WithExample("user", "grant", "alice", "my-podcast");

                user.AddCommand<RevokeCommand>("revoke")
                    .WithDescription("Revoke feed ownership from a user")
                    .WithExample("user", "revoke", "alice", "my-podcast");

                user.AddCommand<RotateKeyCommand>("rotate-key")
                    .WithDescription("Regenerate a user's API key")
                    .WithExample("user", "rotate-key", "john");
            });

            // Alias for backward compatibility
            config.AddCommand<PushCommand>("push")
                .WithDescription("Upload episode(s) to the podcast feed (alias for 'episode push')")
                .WithExample("push", "episode.mp3", "--title", "\"My Episode\"")
                .WithExample("push", "*.mp3", "-x")
                .WithExample("push", "ep1.mp3,ep2.mp3", "-e", "Test");

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
}
