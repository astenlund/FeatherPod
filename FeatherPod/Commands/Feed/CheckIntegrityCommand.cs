using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

/// <summary>
/// Check data integrity - verifies episode metadata loads correctly and audio blobs exist.
/// </summary>
internal sealed class CheckIntegrityCommand : AsyncCommand<CheckIntegritySettings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public override async Task<int> ExecuteAsync(CommandContext context, CheckIntegritySettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Data Integrity Check[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return 1;
        }

        try
        {
            var url = "/api/feeds/check-integrity";
            if (!string.IsNullOrEmpty(settings.FeedId))
            {
                url += $"?feedId={Uri.EscapeDataString(settings.FeedId)}";
            }

            var response = await httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<IntegrityReport>(content, JsonOptions);

                if (result != null)
                {
                    if (!string.IsNullOrEmpty(settings.FeedId))
                    {
                        Out.MarkupLine($"Checking feed: [cyan]{Markup.Escape(settings.FeedId)}[/]");
                        Out.BlankLine();
                    }

                    Out.MarkupLine($"Total episodes: [cyan]{result.TotalEpisodes}[/]");
                    Out.MarkupLine($"Valid episodes: [green]{result.ValidEpisodes}[/]");
                    Out.MarkupLine($"Missing blobs: [yellow]{result.MissingBlobs}[/]");

                    if (result.Issues is { Length: > 0 })
                    {
                        Out.BlankLine();
                        Out.MarkupLine("[bold]Episodes with missing audio blobs:[/]");

                        foreach (var issue in result.Issues)
                        {
                            Out.MarkupLine($"  [grey]{Markup.Escape(issue.FeedId)}[/] / [cyan]{Markup.Escape(issue.Title)}[/]");
                            Out.MarkupLine($"    File: [grey]{Markup.Escape(issue.FileName)}[/]");
                        }
                    }
                    else if (result.MissingBlobs == 0)
                    {
                        Out.BlankLine();
                        Out.Success("All episodes have valid audio blobs!");
                    }
                }
                else
                {
                    Out.WriteLine(content);
                }
            }
            else
            {
                Out.Error($"Check failed: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(errorContent, indent: 2);
                }

                return 1;
            }
        }
        catch (HttpRequestException ex)
        {
            Out.Error($"Check failed: {ex.Message}");

            return 1;
        }

        Out.BlankLine().Flush();

        return 0;
    }

    private record IntegrityReport(int TotalEpisodes, int ValidEpisodes, int MissingBlobs, EpisodeIssue[]? Issues);

    private record EpisodeIssue(string FeedId, string EpisodeId, string FileName, string Title);
}
