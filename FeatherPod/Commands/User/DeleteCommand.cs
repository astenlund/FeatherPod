using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.User;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod User Management - Delete User[/]");
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

        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            Out.Error("User ID cannot be empty");

            return 1;
        }

        var confirm = await AnsiConsole.ConfirmAsync($"Are you sure you want to delete user [cyan]{Markup.Escape(userId)}[/]?", false, cancellationToken);
        if (!confirm)
        {
            Out.Cancelled();

            return 0;
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/users/{Uri.EscapeDataString(userId)}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Out.BlankLine();
                Out.Success("User deleted successfully");
                Out.BlankLine().Flush();

                return 0;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to delete user: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return 1;
        }
        catch (Exception ex)
        {
            Out.Error($"Error deleting user: {ex.Message}");
            Out.BlankLine().Flush();

            return 1;
        }
    }
}
