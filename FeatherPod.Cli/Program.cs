using FeatherPod.Cli.Commands;
using FeatherPod.Cli.Settings;
using Spectre.Console.Cli;

namespace FeatherPod.Cli;

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

            config.AddCommand<PushCommand>("push")
                .WithDescription("Upload episode(s) to the podcast feed")
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
