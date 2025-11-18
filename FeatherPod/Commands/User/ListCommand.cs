using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.User;

internal sealed class ListCommand : AsyncCommand<ListSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - List Users[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        try
        {
            var response = await httpClient.GetAsync("/api/users");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var users = JsonSerializer.Deserialize<JsonElement>(json);

                if (users.ValueKind == JsonValueKind.Array && users.GetArrayLength() == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No users found.[/]");
                    return 0;
                }

                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.AddColumn("[cyan]User ID[/]");
                table.AddColumn("[cyan]Name[/]");
                table.AddColumn("[cyan]Email[/]");
                table.AddColumn("[cyan]Role[/]");
                table.AddColumn("[cyan]Owned Feeds[/]");
                table.AddColumn("[cyan]Last Active[/]");

                foreach (var user in users.EnumerateArray())
                {
                    var id = user.GetProperty("id").GetString() ?? "";
                    var name = user.GetProperty("name").GetString() ?? "";
                    var email = user.GetProperty("email").GetString() ?? "";
                    var role = user.GetProperty("role").GetString() ?? "";

                    var ownedFeeds = "";
                    if (user.TryGetProperty("ownedFeeds", out var feedsElement) && feedsElement.ValueKind == JsonValueKind.Array)
                    {
                        var feeds = feedsElement.EnumerateArray().Select(f => f.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        ownedFeeds = feeds.Count > 0 ? string.Join(", ", feeds) : "-";
                    }
                    else
                    {
                        ownedFeeds = "-";
                    }

                    var lastActive = "-";
                    if (user.TryGetProperty("lastActive", out var lastActiveElement) && lastActiveElement.ValueKind != JsonValueKind.Null)
                    {
                        if (DateTime.TryParse(lastActiveElement.GetString(), out var lastActiveDate))
                        {
                            lastActive = lastActiveDate.ToString("yyyy-MM-dd HH:mm");
                        }
                    }

                    table.AddRow(
                        Markup.Escape(id),
                        Markup.Escape(name),
                        Markup.Escape(email),
                        role == "Admin" ? "[green]Admin[/]" : "[cyan]FeedOwner[/]",
                        Markup.Escape(ownedFeeds),
                        Markup.Escape(lastActive)
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to list users: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error listing users: {ex.Message}");
            return 1;
        }
    }
}
