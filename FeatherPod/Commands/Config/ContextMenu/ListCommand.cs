using FeatherPod.Infrastructure;
using FeatherPod.Settings.Config;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Config.ContextMenu;

#pragma warning disable CA1416 // Platform compatibility - these commands are only registered on Windows (see Program.cs)
internal sealed class ListCommand : Command<ContextMenuListSettings>
{
    public override int Execute(CommandContext context, ContextMenuListSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Context Menu Entries[/]");
        Out.BlankLine();

        var entries = ContextMenuRegistry.GetInstalled();

        if (entries.Count == 0)
        {
            Out.MarkupLine("[grey]No context menu entries installed.[/]");
            Out.MarkupLine("[grey]Use 'featherpod config context-menu install' to register one.[/]");
            Out.BlankLine().Flush();

            return 0;
        }

        var table = new Table()
            .AddColumn("Feed ID")
            .AddColumn("Title")
            .AddColumn("Environment")
            .Border(TableBorder.Rounded);

        foreach (var entry in entries)
        {
            table.AddRow(
                Markup.Escape(entry.FeedId),
                Markup.Escape(entry.FeedTitle),
                Markup.Escape(entry.Environment));
        }

        Out.Write(table);
        Out.BlankLine();
        Out.MarkupLine($"[grey]{entries.Count} {(entries.Count == 1 ? "entry" : "entries")} registered across {AudioExtensions.All.Length} audio extensions each[/]");
        Out.BlankLine().Flush();

        return 0;
    }
}
