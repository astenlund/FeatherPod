using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

using EpisodeModel = FeatherPod.Shared.Models.Episode;
using FeedConfig = FeatherPod.Shared.Models.FeedConfig;

namespace FeatherPod.Commands.Episode;

/// <summary>
/// Shared resolution and note operations reused by the CLI note commands and InteractiveCommand.
/// </summary>
internal static class NoteCommandCore
{
    internal static async Task<(HttpClient Client, FeedConfig Feed, EpisodeModel Episode)?> ResolveAsync(
        string? environment, string? feedId, string? episodeId)
    {
        var env = EnvironmentHelpers.GetEnvironment(environment);
        if (env == null)
        {
            return null;
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return null;
        }

        var feed = !string.IsNullOrEmpty(feedId)
            ? await FeedHelpers.GetFeedByIdAsync(httpClient, feedId)
            : await FeedHelpers.SelectFeedAsync(httpClient);

        if (feed == null)
        {
            Out.Error(!string.IsNullOrEmpty(feedId)
                ? $"Feed '{Markup.Escape(feedId)}' not found."
                : "No feeds available.");
            Out.BlankLine().Flush();

            return null;
        }

        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, feed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            Out.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(feed.Title)}[/]' has no episodes.[/]");
            Out.BlankLine().Flush();

            return null;
        }

        EpisodeModel? episode;
        if (!string.IsNullOrEmpty(episodeId))
        {
            episode = episodes.FirstOrDefault(e => e.Id == episodeId);
            if (episode == null)
            {
                Out.Error($"Episode '{Markup.Escape(episodeId)}' not found in feed '{Markup.Escape(feed.Id)}'.");
                Out.BlankLine().Flush();

                return null;
            }
        }
        else
        {
            episode = EpisodeHelpers.SelectEpisodeSingle(episodes);
            if (episode == null)
            {
                Out.Cancelled();
                Out.BlankLine().Flush();

                return null;
            }
        }

        return (httpClient, feed, episode);
    }

    internal static void ShowNote(EpisodeModel episode)
    {
        if (string.IsNullOrEmpty(episode.Note))
        {
            Out.MarkupLine($"[grey]No note set for[/] [cyan]{Markup.Escape(episode.Title)}[/]");
        }
        else
        {
            Out.MarkupLine($"  Note: [grey]{Markup.Escape(episode.Note)}[/]");
        }
    }

    internal static async Task<EpisodeOperationResult> SetNoteAsync(
        HttpClient httpClient, string feedId, EpisodeModel episode, string note,
        CancellationToken cancellationToken)
    {
        var result = await EpisodeHelpers.UpdateEpisodeNoteAsync(httpClient, feedId, episode.Id, note, cancellationToken);

        if (result.Success)
        {
            Out.Success($"Note updated for [cyan]{Markup.Escape(episode.Title)}[/]");
        }

        return result;
    }

    internal static async Task<EpisodeOperationResult> ClearNoteAsync(
        HttpClient httpClient, string feedId, EpisodeModel episode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(episode.Note))
        {
            Out.MarkupLine("[grey]No note to clear.[/]");

            return new() { Success = true, EpisodeId = episode.Id };
        }

        var result = await EpisodeHelpers.UpdateEpisodeNoteAsync(httpClient, feedId, episode.Id, string.Empty, cancellationToken);

        if (result.Success)
        {
            Out.Success($"Note cleared for [cyan]{Markup.Escape(episode.Title)}[/]");
        }

        return result;
    }
}

internal sealed class NoteSetCommand : AsyncCommand<NoteSetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NoteSetSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Note[/]");
        Out.BlankLine();

        var resolved = await NoteCommandCore.ResolveAsync(settings.Environment, settings.FeedId, settings.EpisodeId);
        if (resolved == null)
        {
            return 1;
        }

        var (httpClient, feed, episode) = resolved.Value;

        Out.BlankLine();
        Out.MarkupLine($"  Title: [cyan]{Markup.Escape(episode.Title)}[/]");
        NoteCommandCore.ShowNote(episode);
        Out.BlankLine();

        var note = settings.Note;
        if (note == null)
        {
            note = LineEditor.Edit("Note: ", episode.Note ?? "");
            if (note == null)
            {
                Out.Cancelled();
                Out.BlankLine().Flush();

                return 1;
            }
        }

        var result = await NoteCommandCore.SetNoteAsync(httpClient, feed.Id, episode, note.Trim(), cancellationToken);
        Out.BlankLine().Flush();

        return result.Success ? 0 : 1;
    }
}

internal sealed class NoteGetCommand : AsyncCommand<NoteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NoteSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Note[/]");
        Out.BlankLine();

        var resolved = await NoteCommandCore.ResolveAsync(settings.Environment, settings.FeedId, settings.EpisodeId);
        if (resolved == null)
        {
            return 1;
        }

        var (_, _, episode) = resolved.Value;

        Out.BlankLine();
        Out.MarkupLine($"  Title: [cyan]{Markup.Escape(episode.Title)}[/]");
        NoteCommandCore.ShowNote(episode);
        Out.BlankLine().Flush();

        return 0;
    }
}

internal sealed class NoteClearCommand : AsyncCommand<NoteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NoteSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Note[/]");
        Out.BlankLine();

        var resolved = await NoteCommandCore.ResolveAsync(settings.Environment, settings.FeedId, settings.EpisodeId);
        if (resolved == null)
        {
            return 1;
        }

        var (httpClient, feed, episode) = resolved.Value;

        var result = await NoteCommandCore.ClearNoteAsync(httpClient, feed.Id, episode, cancellationToken);
        Out.BlankLine().Flush();

        return result.Success ? 0 : 1;
    }
}
