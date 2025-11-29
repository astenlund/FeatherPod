using System.Text;
using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.User;

internal sealed class GrantCommand : AsyncCommand<GrantSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GrantSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod User Management - Grant Feed Ownership[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var userId = settings.UserId.Trim();
        var feedId = settings.FeedId.Trim();

        if (string.IsNullOrWhiteSpace(userId))
        {
            Out.Error("User ID cannot be empty");

            return 1;
        }

        if (string.IsNullOrWhiteSpace(feedId))
        {
            Out.Error("Feed ID cannot be empty");

            return 1;
        }

        var requestBody = new { feedId };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId)}/feeds", content);

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Granted feed [cyan]{Markup.Escape(feedId)}[/] ownership to user [cyan]{Markup.Escape(userId)}[/]");

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Error($"Failed to grant feed ownership: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(Markup.Escape(errorContent));
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error granting feed ownership: {ex.Message}");
            return 1;
        }
    }
}
