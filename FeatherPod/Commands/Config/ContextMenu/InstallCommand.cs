using System.Security;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Config;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Config.ContextMenu;

#pragma warning disable CA1416 // Platform compatibility - these commands are only registered on Windows (see Program.cs)
internal sealed class InstallCommand : AsyncCommand<ContextMenuInstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ContextMenuInstallSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Context Menu Install[/]");
        Out.BlankLine();

        var cliPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(cliPath))
        {
            Out.Error("Cannot determine executable path. Context menu registration requires a published executable.");
            Out.BlankLine();
            Out.MarkupLine("[grey]Run 'dotnet publish' first, then use the published featherpod.exe.[/]");
            Out.BlankLine().Flush();

            return 1;
        }

        var cliDir = Path.GetDirectoryName(cliPath)!;
        var launcherPath = Path.Combine(cliDir, "featherpod-launcher.exe");

        if (!File.Exists(launcherPath))
        {
            Out.Error("featherpod-launcher.exe not found alongside featherpod.exe.");
            Out.BlankLine();
            Out.MarkupLine($"[grey]Expected at: [cyan]{Markup.Escape(launcherPath)}[/][/]");
            Out.MarkupLine("[grey]Publish the FeatherPod.Launcher project to the same directory as featherpod.exe.[/]");
            Out.BlankLine().Flush();

            return 1;
        }

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env is null)
        {
            return 1;
        }

        var (httpClient, currentUser) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient is null)
        {
            return 1;
        }

        try
        {
            var feed = !string.IsNullOrEmpty(settings.FeedId)
                ? await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
                : await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);

            if (feed is null)
            {
                if (!string.IsNullOrEmpty(settings.FeedId))
                {
                    Out.Error($"Feed not found: {Markup.Escape(settings.FeedId)}");
                }
                else
                {
                    Out.Cancelled();
                }

                Out.BlankLine().Flush();

                return 1;
            }

            try
            {
                ContextMenuRegistry.Install(feed.Id, feed.Title, launcherPath, cliPath, env);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                Out.Error($"Failed to write registry entries: {Markup.Escape(ex.Message)}");
                Out.BlankLine().Flush();

                return 1;
            }

            Out.BlankLine();
            Out.Success($"Registered context menu for [cyan]{Markup.Escape(feed.Title)}[/] ({AudioExtensions.All.Length} audio extensions)");
            Out.MarkupLine($"[grey]Feed: {Markup.Escape(feed.Id)}, Environment: {Markup.Escape(env)}[/]");
            Out.BlankLine().Flush();

            return 0;
        }
        finally
        {
            httpClient.Dispose();
        }
    }
}
