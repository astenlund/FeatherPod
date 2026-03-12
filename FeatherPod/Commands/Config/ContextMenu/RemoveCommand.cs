using System.Security;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Config;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Config.ContextMenu;

#pragma warning disable CA1416 // Platform compatibility - these commands are only registered on Windows (see Program.cs)
internal sealed class RemoveCommand : Command<ContextMenuRemoveSettings>
{
    public override int Execute(CommandContext context, ContextMenuRemoveSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Context Menu Remove[/]");
        Out.BlankLine();

        if (settings.All)
        {
            var entries = ContextMenuRegistry.GetInstalled();
            if (entries.Count == 0)
            {
                Out.MarkupLine("[grey]No context menu entries to remove.[/]");
                Out.BlankLine().Flush();

                return 0;
            }

            try
            {
                ContextMenuRegistry.RemoveAll();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                Out.Error($"Failed to remove registry entries: {Markup.Escape(ex.Message)}");
                Out.BlankLine().Flush();

                return 1;
            }

            Out.Success($"Removed all context menu entries ({entries.Count} feed(s))");
            Out.BlankLine().Flush();

            return 0;
        }

        if (!string.IsNullOrEmpty(settings.FeedId))
        {
            try
            {
                ContextMenuRegistry.Remove(settings.FeedId);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                Out.Error($"Failed to remove registry entries: {Markup.Escape(ex.Message)}");
                Out.BlankLine().Flush();

                return 1;
            }

            Out.Success($"Removed context menu entry for [cyan]{Markup.Escape(settings.FeedId)}[/]");
            Out.BlankLine().Flush();

            return 0;
        }

        var installed = ContextMenuRegistry.GetInstalled();
        if (installed.Count == 0)
        {
            Out.MarkupLine("[grey]No context menu entries to remove.[/]");
            Out.BlankLine().Flush();

            return 0;
        }

        var menu = new MenuBuilder<string?>()
            .WithTitle("Select entry to remove:")
            .WithHint("(arrow keys, Enter to select)")
            .AllowCancel();

        foreach (var entry in installed)
        {
            menu.AddOption(null, $"[cyan]{Markup.Escape(entry.FeedTitle)}[/] [grey]({Markup.Escape(entry.FeedId)}, {Markup.Escape(entry.Environment)})[/]", entry.FeedId);
        }

        var selectedFeedId = menu.Show();
        if (selectedFeedId is null)
        {
            Out.Cancelled();
            Out.BlankLine().Flush();

            return 0;
        }

        try
        {
            ContextMenuRegistry.Remove(selectedFeedId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            Out.Error($"Failed to remove registry entries: {Markup.Escape(ex.Message)}");
            Out.BlankLine().Flush();

            return 1;
        }

        Out.Success($"Removed context menu entry for [cyan]{Markup.Escape(selectedFeedId)}[/]");
        Out.BlankLine().Flush();

        return 0;
    }
}
