using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.User;

internal sealed class RevokeCommand : AsyncCommand<RevokeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RevokeSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod User Management - Revoke Feed Ownership[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            Out.Error("User ID cannot be empty");

            return 1;
        }

        var feedId = settings.FeedId?.Trim();
        if (string.IsNullOrWhiteSpace(feedId))
        {
            var feed = await FeedHelpers.SelectFeedAsync(httpClient);
            if (feed == null)
            {
                Out.Error("No feeds available.");

                return 1;
            }
            feedId = feed.Id;
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/users/{Uri.EscapeDataString(userId)}/feeds/{Uri.EscapeDataString(feedId)}");

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Revoked feed [cyan]{Markup.Escape(feedId)}[/] ownership from user [cyan]{Markup.Escape(userId)}[/]");

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Error($"Failed to revoke feed ownership: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(Markup.Escape(errorContent));
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error revoking feed ownership: {ex.Message}");
            return 1;
        }
    }
}
