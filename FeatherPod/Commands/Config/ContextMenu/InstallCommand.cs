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

        var bridgePath = BridgeResource.ExtractBridge();
        if (bridgePath is null)
        {
            Out.Error("featherpod-bridge.exe could not be extracted or found alongside featherpod.exe.");
            Out.BlankLine();
            Out.MarkupLine("[grey]Publish the CLI using Publish-Cli.ps1 to embed the bridge binary.[/]");
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
                : await FeedHelpers.SelectFeedAsync(httpClient, currentUser: currentUser);

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
                ContextMenuRegistry.Install(feed.Id, feed.Title, bridgePath, cliPath, env, settings.DeleteAfter);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                Out.Error($"Failed to write registry entries: {Markup.Escape(ex.Message)}");
                Out.BlankLine().Flush();

                return 1;
            }

            Out.BlankLine();
            Out.Success($"Registered context menu for [cyan]{Markup.Escape(feed.Title)}[/] ({AudioExtensions.All.Length} audio extensions)");
            Out.BlankLine();
            var deleteAfterHint = settings.DeleteAfter ? ", Delete after upload: yes" : "";
            Out.MarkupLine($"[grey]Feed: {Markup.Escape(feed.Id)}, Environment: {Markup.Escape(env)}{deleteAfterHint}[/]");
            Out.BlankLine().Flush();

            return 0;
        }
        finally
        {
            httpClient.Dispose();
        }
    }
}
