using System.Reflection;
using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using EpisodeDeleteCommand = FeatherPod.Commands.Episode.DeleteCommand;
using EpisodeListCommand = FeatherPod.Commands.Episode.ListCommand;
using FeedUpdateCommand = FeatherPod.Commands.Feed.UpdateCommand;
using FeedCreateCommand = FeatherPod.Commands.Feed.CreateCommand;
using FeedDeleteCommand = FeatherPod.Commands.Feed.DeleteCommand;
using FeedRenameCommand = FeatherPod.Commands.Feed.RenameCommand;

namespace FeatherPod.Commands;

internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment, useDefault: true);
        if (env == null) return 1;

        var autoConnect = PreferencesHelpers.GetAutoConnectEnabled(env) ?? true;
        var apiUrl = EnvironmentHelpers.BuildConfiguration(env)["Api:BaseUrl"]?.TrimEnd('/') + "/api";

        // Show header and optionally connect
        var (httpClient, currentUser, isConnected, serverVersion) = await ShowHeader(
            env, apiUrl, currentFeed: null, shouldConnect: autoConnect);

        // Select initial feed (only if connected, suppress "no feeds" message since header shows status)
        FeedConfig? currentFeed = null;
        if (isConnected && httpClient != null)
        {
            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, currentUser: currentUser, showNoFeedsMessage: false);
        }

        // Main menu loop
        while (true)
        {
            // Redraw header with current feed status
            await ShowHeader(env, apiUrl, currentFeed, shouldConnect: false, currentlyConnected: isConnected, existingServerVersion: serverVersion);

            var choice = ShowMenu(currentFeed, isConnected, currentUser);

            switch (choice)
            {
                case MenuChoice.List:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Settings to connect.[/]");
                    }
                    else if (currentFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feed selected. Use 'M: Manage Feeds' to create one.[/]");
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        await EpisodeListCommand.ListEpisodesAsync(httpClient, currentFeed);
                    }
                    WaitForKeyPress();
                    break;

                case MenuChoice.Push:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Settings to connect.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    // Select feed if none selected
                    var pushFeed = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                    if (pushFeed == null)
                    {
                        WaitForKeyPress();
                        break;
                    }
                    currentFeed = pushFeed;

                    // Prompt for file path(s)
                    var filePattern = AnsiConsole.Prompt(
                        new TextPrompt<string>("Enter file path(s):")
                            .AllowEmpty());

                    if (string.IsNullOrWhiteSpace(filePattern))
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    // Expand file patterns
                    var filesToUpload = EpisodeHelpers.ExpandFilePatterns(filePattern);
                    if (filesToUpload.Count == 0)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] No files found matching pattern: {Markup.Escape(filePattern)}");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.MarkupLine($"Found [bold]{filesToUpload.Count}[/] file(s)");
                    AnsiConsole.WriteLine();

                    // Confirm files
                    var fileListDisplay = filesToUpload.Count <= 5
                        ? string.Join(", ", filesToUpload.Select(f => $"[cyan]{Markup.Escape(Path.GetFileName(f))}[/]"))
                        : $"[bold]{filesToUpload.Count}[/] files";

                    var confirmUpload = new MenuBuilder<bool?>()
                        .WithTitle($"Upload {fileListDisplay} to feed [cyan]{Markup.Escape(pushFeed.Title)}[/]?")
                        .WithHint("(Y/N, Esc to cancel)")
                        .AddOption("Y", "Yes", true)
                        .AddOption("N", "No", false)
                        .AllowCancel(true, false)
                        .Show();

                    if (confirmUpload != true)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // For single file, offer optional metadata prompts
                    string? pushTitle = null;
                    string? pushDescription = null;
                    string? pushSummary = null;

                    if (filesToUpload.Count == 1)
                    {
                        pushTitle = AnsiConsole.Prompt(
                            new TextPrompt<string>("Title [grey](Enter to use filename)[/]:")
                                .AllowEmpty());

                        pushDescription = AnsiConsole.Prompt(
                            new TextPrompt<string>("Description [grey](optional)[/]:")
                                .AllowEmpty());

                        if (!string.IsNullOrEmpty(pushDescription))
                        {
                            pushSummary = AnsiConsole.Prompt(
                                new TextPrompt<string>("Summary [grey](optional, defaults to description)[/]:")
                                    .AllowEmpty());
                        }

                        AnsiConsole.WriteLine();
                    }

                    // Prompt for date source
                    var dateSource = new MenuBuilder<bool?>()
                        .WithTitle("Published date source:")
                        .WithHint("(C/F, Esc to cancel)")
                        .AddOption("C", "Current date/time", false)
                        .AddOption("F", "Extract from file metadata", true)
                        .AllowCancel()
                        .Show();

                    if (dateSource == null)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // Prompt for normalization (L=Local, S=Server, N=None)
                    var normalizeChoice = new MenuBuilder<string?>()
                        .WithTitle("Normalize audio to -16 LUFS?")
                        .WithHint("(L/S/N, Esc to cancel)")
                        .AddOption("L", "Local - Normalize on this machine", "local")
                        .AddOption("S", "Server - Normalize on server", "server")
                        .AddOption("N", "None - Keep original", "none")
                        .AllowCancel()
                        .Show();

                    if (normalizeChoice == null)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // Temporarily set normalization preference for upload (only for local normalization)
                    var originalNormPref = PreferencesHelpers.GetNormalizationEnabled(env);
                    var useServerNormalize = normalizeChoice == "server";
                    PreferencesHelpers.SetNormalizationEnabled(env, normalizeChoice == "local");

                    try
                    {
                        var configuration = EnvironmentHelpers.BuildConfiguration(env);
                        var uploadSettings = new PushSettings
                        {
                            Files = filePattern,
                            Title = string.IsNullOrWhiteSpace(pushTitle) ? null : pushTitle.Trim(),
                            Description = string.IsNullOrWhiteSpace(pushDescription) ? null : pushDescription.Trim(),
                            Summary = string.IsNullOrWhiteSpace(pushSummary) ? null : pushSummary.Trim(),
                            ExtractDateFromFile = dateSource,
                            ServerNormalize = useServerNormalize
                        };

                        var successCount = 0;
                        var failureCount = 0;

                        foreach (var file in filesToUpload)
                        {
                            var success = await EpisodeHelpers.UploadEpisodeAsync(httpClient, configuration, env, pushFeed, file, uploadSettings);
                            if (success)
                                successCount++;
                            else
                                failureCount++;

                            AnsiConsole.WriteLine();
                        }

                        // Summary
                        if (successCount > 0)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] Successfully uploaded: {successCount}");
                        }
                        if (failureCount > 0)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Failed: {failureCount}");
                        }
                    }
                    finally
                    {
                        // Restore original normalization preference
                        if (originalNormPref.HasValue)
                        {
                            PreferencesHelpers.SetNormalizationEnabled(env, originalNormPref.Value);
                        }
                    }

                    WaitForKeyPress();
                    break;

                case MenuChoice.MoveCopy:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Settings to connect.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    var moveCopyChoice = new MenuBuilder<string?>()
                        .WithTitle("Move/Copy episodes:")
                        .WithHint("(M/C, Esc to go back)")
                        .AddOption("M", "Move episodes", "move")
                        .AddOption("C", "Copy episodes", "copy")
                        .AllowCancel()
                        .Show();

                    if (moveCopyChoice == null)
                        break;

                    var isMove = moveCopyChoice == "move";
                    var actionVerb = isMove ? "Move" : "Copy";
                    var actionPast = isMove ? "Moved" : "Copied";

                    // Select source feed
                    var sourceFeed = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, contextMessage: "Select source feed:", currentUser: currentUser);
                    if (sourceFeed == null)
                    {
                        WaitForKeyPress();
                        break;
                    }
                    currentFeed = sourceFeed;

                    // Get episodes from source
                    var sourceEpisodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, sourceFeed.Id);
                    if (sourceEpisodes == null || sourceEpisodes.Count == 0)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(sourceFeed.Title)}[/]' has no episodes.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    // Select episodes (multi-select)
                    var episodesToProcess = EpisodeHelpers.SelectEpisodesMulti(sourceEpisodes);
                    if (episodesToProcess.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // Get target feeds (exclude source for move)
                    var allFeeds = await FeedHelpers.GetFeedsAsync(httpClient);
                    var availableTargets = isMove
                        ? allFeeds.Where(f => f.Id != sourceFeed.Id).ToList()
                        : allFeeds;

                    if (availableTargets.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] No target feeds available.");
                        WaitForKeyPress();
                        break;
                    }

                    // Select target feed
                    var targetMenu = new MenuBuilder<FeedConfig?>()
                        .WithTitle("Select target feed:")
                        .AllowCancel();

                    foreach (var feed in availableTargets)
                    {
                        targetMenu.AddOption(null, Markup.Escape(feed.Title), feed);
                    }

                    var targetFeed = targetMenu.Show();
                    if (targetFeed == null)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    // Validate for copy: can't copy to same feed
                    if (!isMove && sourceFeed.Id == targetFeed.Id)
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] Cannot copy episodes within the same feed.");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // Confirmation
                    var epWord = episodesToProcess.Count == 1 ? "episode" : "episodes";
                    var confirmAction = new MenuBuilder<bool?>()
                        .WithTitle($"{actionVerb} [cyan]{episodesToProcess.Count}[/] {epWord} from '[cyan]{Markup.Escape(sourceFeed.Title)}[/]' to '[cyan]{Markup.Escape(targetFeed.Title)}[/]'?")
                        .WithHint("(Y/N, Esc to cancel)")
                        .AddOption("Y", "Yes", true)
                        .AddOption("N", "No", false)
                        .AllowCancel(true, false)
                        .Show();

                    if (confirmAction != true)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // Process episodes with progress bar
                    var mcSuccessCount = 0;
                    var mcFailureCount = 0;

                    await AnsiConsole.Progress()
                        .Columns(
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new SpinnerColumn())
                        .StartAsync(async ctx =>
                        {
                            var task = ctx.AddTask($"{actionVerb}ing {episodesToProcess.Count} {epWord}", maxValue: episodesToProcess.Count);

                            foreach (var episode in episodesToProcess)
                            {
                                bool success;
                                if (isMove)
                                {
                                    success = await EpisodeHelpers.MoveEpisodeAsync(httpClient, sourceFeed.Id, episode.Id, targetFeed.Id);
                                }
                                else
                                {
                                    success = await EpisodeHelpers.CopyEpisodeAsync(httpClient, sourceFeed.Id, episode.Id, targetFeed.Id);
                                }

                                if (success)
                                    mcSuccessCount++;
                                else
                                    mcFailureCount++;

                                task.Increment(1);
                            }
                        });

                    AnsiConsole.WriteLine();

                    // Summary
                    if (mcSuccessCount > 0)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] {actionPast} {mcSuccessCount} of {episodesToProcess.Count} episode(s) successfully");
                    }
                    if (mcFailureCount > 0)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Failed to {actionVerb.ToLower()} {mcFailureCount} episode(s)");
                    }

                    WaitForKeyPress();
                    break;

                case MenuChoice.UserManagement:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Settings to connect.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    var userChoice = new MenuBuilder<string?>()
                        .WithTitle("User management:")
                        .WithHint("(arrow keys or highlighted letter, Esc to go back)")
                        .AddOption("L", "List users", "list")
                        .AddOption("C", "Create user", "create")
                        .AddOption("D", "Delete user", "delete")
                        .AddOption("G", "Grant feed ownership", "grant")
                        .AddOption("R", "Revoke feed ownership", "revoke")
                        .AddOption("K", "Rotate API key", "rotate")
                        .AllowCancel()
                        .Show();

                    if (userChoice == null)
                        break;

                    switch (userChoice)
                    {
                        case "list":
                            try
                            {
                                var listResponse = await httpClient.GetAsync("/api/users", cancellationToken);
                                if (listResponse.IsSuccessStatusCode)
                                {
                                    var listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken);
                                    var users = JsonSerializer.Deserialize<JsonElement>(listJson);

                                    if (users.ValueKind == JsonValueKind.Array && users.GetArrayLength() == 0)
                                    {
                                        AnsiConsole.MarkupLine("[yellow]No users found.[/]");
                                    }
                                    else
                                    {
                                        var table = new Table();
                                        table.Border(TableBorder.Rounded);
                                        table.AddColumn("[bold]User ID[/]");
                                        table.AddColumn("[bold]Name[/]");
                                        table.AddColumn("[bold]Role[/]");
                                        table.AddColumn("[bold]Owned Feeds[/]");

                                        foreach (var user in users.EnumerateArray())
                                        {
                                            var id = user.GetProperty("id").GetString() ?? "";
                                            var name = user.GetProperty("name").GetString() ?? "";
                                            var role = user.GetProperty("role").GetString() ?? "";

                                            var ownedFeeds = "-";
                                            if (user.TryGetProperty("ownedFeeds", out var feedsEl) && feedsEl.ValueKind == JsonValueKind.Array)
                                            {
                                                var feeds = feedsEl.EnumerateArray().Select(f => f.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                                                ownedFeeds = feeds.Count > 0 ? string.Join(", ", feeds) : "-";
                                            }

                                            table.AddRow(
                                                Markup.Escape(id),
                                                Markup.Escape(name),
                                                role == "Admin" ? "[grey]Admin[/]" : "[grey]FeedOwner[/]",
                                                Markup.Escape(ownedFeeds)
                                            );
                                        }

                                        AnsiConsole.Write(table);
                                    }
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to list users: {listResponse.StatusCode}");
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
                            }
                            WaitForKeyPress();
                            break;

                        case "create":
                            var newUserId = AnsiConsole.Prompt(new TextPrompt<string>("User [bold]ID[/]:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(newUserId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var newName = AnsiConsole.Ask<string>("Display [bold]name[/]:");
                            var newEmail = AnsiConsole.Prompt(
                                new TextPrompt<string>("[bold]Email[/] (optional):")
                                    .AllowEmpty());

                            var newRole = new MenuBuilder<string?>()
                                .WithTitle("Select role:")
                                .AddOption("A", "Admin", "Admin")
                                .AddOption("F", "FeedOwner", "FeedOwner")
                                .AllowCancel()
                                .Show();

                            if (newRole == null)
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var newOwnedFeeds = new List<string>();
                            if (newRole == "FeedOwner")
                            {
                                var feedsInput = AnsiConsole.Ask("Feed IDs to own [grey](comma-separated, or Enter for none)[/]:", string.Empty);
                                if (!string.IsNullOrWhiteSpace(feedsInput))
                                {
                                    newOwnedFeeds = feedsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                                }
                            }

                            try
                            {
                                var createBody = JsonSerializer.Serialize(new
                                {
                                    id = newUserId.Trim(),
                                    name = newName.Trim(),
                                    email = string.IsNullOrWhiteSpace(newEmail) ? null : newEmail.Trim(),
                                    role = newRole,
                                    ownedFeeds = newOwnedFeeds
                                });

                                var createResponse = await httpClient.PostAsync("/api/users",
                                    new StringContent(createBody, System.Text.Encoding.UTF8, "application/json"), cancellationToken);

                                if (createResponse.IsSuccessStatusCode)
                                {
                                    var createJson = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                                    var createData = JsonSerializer.Deserialize<JsonElement>(createJson);

                                    AnsiConsole.MarkupLine("[green]✓[/] User created successfully");
                                    AnsiConsole.WriteLine();

                                    if (createData.TryGetProperty("apiKey", out var apiKeyEl))
                                    {
                                        AnsiConsole.MarkupLine($"[yellow bold]API Key (save now, won't be shown again):[/]");
                                        AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(apiKeyEl.GetString() ?? "")}[/]");
                                    }
                                }
                                else
                                {
                                    var errorContent = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to create user: {createResponse.StatusCode}");
                                    if (!string.IsNullOrEmpty(errorContent))
                                    {
                                        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(errorContent)}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
                            }
                            WaitForKeyPress();
                            break;

                        case "delete":
                            var deleteUserId = AnsiConsole.Prompt(new TextPrompt<string>("User ID to delete:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(deleteUserId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var confirmDelete = new MenuBuilder<bool?>()
                                .WithTitle($"Delete user [cyan]{Markup.Escape(deleteUserId)}[/]?")
                                .WithHint("(Y/N)")
                                .AddOption("Y", "Yes", true)
                                .AddOption("N", "No", false)
                                .AllowCancel(true, false)
                                .Show();

                            if (confirmDelete != true)
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            try
                            {
                                var deleteResponse = await httpClient.DeleteAsync($"/api/users/{Uri.EscapeDataString(deleteUserId.Trim())}", cancellationToken);
                                AnsiConsole.MarkupLine(deleteResponse.IsSuccessStatusCode
                                    ? $"[green]✓[/] User '{Markup.Escape(deleteUserId)}' deleted"
                                    : $"[red]✗[/] Failed to delete user: {deleteResponse.StatusCode}");
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
                            }
                            WaitForKeyPress();
                            break;

                        case "grant":
                            var grantUserId = AnsiConsole.Prompt(new TextPrompt<string>("User ID to grant ownership:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(grantUserId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var grantFeedId = AnsiConsole.Prompt(new TextPrompt<string>("Feed ID to grant:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(grantFeedId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            try
                            {
                                var grantBody = JsonSerializer.Serialize(new { feedId = grantFeedId.Trim() });
                                var grantResponse = await httpClient.PostAsync(
                                    $"/api/users/{Uri.EscapeDataString(grantUserId.Trim())}/feeds",
                                    new StringContent(grantBody, System.Text.Encoding.UTF8, "application/json"), cancellationToken);

                                AnsiConsole.MarkupLine(grantResponse.IsSuccessStatusCode
                                    ? $"[green]✓[/] Feed '{Markup.Escape(grantFeedId)}' granted to '{Markup.Escape(grantUserId)}'"
                                    : $"[red]✗[/] Failed to grant: {grantResponse.StatusCode}");
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
                            }
                            WaitForKeyPress();
                            break;

                        case "revoke":
                            var revokeUserId = AnsiConsole.Prompt(new TextPrompt<string>("User ID to revoke from:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(revokeUserId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var revokeFeedId = AnsiConsole.Prompt(new TextPrompt<string>("Feed ID to revoke:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(revokeFeedId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            try
                            {
                                var revokeResponse = await httpClient.DeleteAsync(
                                    $"/api/users/{Uri.EscapeDataString(revokeUserId.Trim())}/feeds/{Uri.EscapeDataString(revokeFeedId.Trim())}", cancellationToken);

                                AnsiConsole.MarkupLine(revokeResponse.IsSuccessStatusCode
                                    ? $"[green]✓[/] Feed '{Markup.Escape(revokeFeedId)}' revoked from '{Markup.Escape(revokeUserId)}'"
                                    : $"[red]✗[/] Failed to revoke: {revokeResponse.StatusCode}");
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
                            }
                            WaitForKeyPress();
                            break;

                        case "rotate":
                            var rotateUserId = AnsiConsole.Prompt(new TextPrompt<string>("User ID to rotate key for:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(rotateUserId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var confirmRotate = new MenuBuilder<bool?>()
                                .WithTitle($"Rotate API key for [cyan]{Markup.Escape(rotateUserId)}[/]?")
                                .WithHint("(Y/N)")
                                .AddOption("Y", "Yes", true)
                                .AddOption("N", "No", false)
                                .AllowCancel(true, false)
                                .Show();

                            if (confirmRotate != true)
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            try
                            {
                                var rotateResponse = await httpClient.PostAsync(
                                    $"/api/users/{Uri.EscapeDataString(rotateUserId.Trim())}/key/regenerate", null, cancellationToken);

                                if (rotateResponse.IsSuccessStatusCode)
                                {
                                    var rotateJson = await rotateResponse.Content.ReadAsStringAsync(cancellationToken);
                                    var rotateData = JsonSerializer.Deserialize<JsonElement>(rotateJson);

                                    AnsiConsole.MarkupLine("[green]✓[/] API key rotated");
                                    AnsiConsole.WriteLine();

                                    if (rotateData.TryGetProperty("apiKey", out var newKeyEl))
                                    {
                                        AnsiConsole.MarkupLine($"[yellow bold]New API Key:[/]");
                                        AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(newKeyEl.GetString() ?? "")}[/]");
                                    }
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to rotate key: {rotateResponse.StatusCode}");
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error: {ex.Message}");
                            }
                            WaitForKeyPress();
                            break;
                    }
                    break;

                case MenuChoice.Delete:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Settings to connect.[/]");
                    }
                    else if (currentFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feed selected. Use 'M: Manage Feeds' to create one.[/]");
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, currentFeed.Id);
                        if (episodes == null || episodes.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]No episodes to delete.[/]");
                        }
                        else
                        {
                            var selected = EpisodeHelpers.SelectEpisodesMulti(episodes);
                            if (selected.Count == 0)
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                            }
                            else if (selected.Count == 1)
                            {
                                await EpisodeDeleteCommand.DeleteEpisodeAsync(httpClient, currentFeed.Id, selected[0], cancellationToken: cancellationToken);
                            }
                            else
                            {
                                // Batch delete with confirmation
                                AnsiConsole.WriteLine();
                                var confirmBatch = new MenuBuilder<bool?>()
                                    .WithTitle($"Delete [cyan]{selected.Count}[/] episodes from [cyan]{Markup.Escape(currentFeed.Title)}[/]?")
                                    .WithHint("(Y/N, Esc to cancel)")
                                    .AddOption("Y", "Yes", true)
                                    .AddOption("N", "No", false)
                                    .AllowCancel(true, false)
                                    .Show();

                                if (confirmBatch != true)
                                {
                                    AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                }
                                else
                                {
                                    AnsiConsole.WriteLine();

                                    var delSuccessCount = 0;
                                    var delFailureCount = 0;

                                    await AnsiConsole.Progress()
                                        .Columns(
                                            new TaskDescriptionColumn(),
                                            new ProgressBarColumn(),
                                            new PercentageColumn(),
                                            new SpinnerColumn())
                                        .StartAsync(async ctx =>
                                        {
                                            var task = ctx.AddTask($"Deleting {selected.Count} episodes", maxValue: selected.Count);

                                            foreach (var episode in selected)
                                            {
                                                try
                                                {
                                                    var deleteResponse = await httpClient.DeleteAsync(
                                                        $"/api/feeds/{Uri.EscapeDataString(currentFeed.Id)}/episodes/{Uri.EscapeDataString(episode.Id)}", cancellationToken);

                                                    if (deleteResponse.IsSuccessStatusCode)
                                                        delSuccessCount++;
                                                    else
                                                        delFailureCount++;
                                                }
                                                catch
                                                {
                                                    delFailureCount++;
                                                }

                                                task.Increment(1);
                                            }
                                        });

                                    AnsiConsole.WriteLine();

                                    if (delSuccessCount > 0)
                                    {
                                        AnsiConsole.MarkupLine($"[green]✓[/] Deleted {delSuccessCount} of {selected.Count} episode(s)");
                                    }

                                    if (delFailureCount > 0)
                                    {
                                        AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete {delFailureCount} episode(s)");
                                    }
                                }
                            }
                        }
                    }

                    WaitForKeyPress();

                    break;

                case MenuChoice.SwitchFeed:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Preferences to connect.[/]");
                        WaitForKeyPress();
                    }
                    else
                    {
                        var newFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                        if (newFeed != null)
                        {
                            currentFeed = newFeed;
                            // No pause - feed title shows in menu header
                        }
                        else
                        {
                            // No feeds available message was shown
                            WaitForKeyPress();
                        }
                    }
                    break;

                case MenuChoice.ManageFeeds:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Preferences to connect.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    var manageChoice = new MenuBuilder<string?>()
                        .WithTitle("Manage Feeds:")
                        .WithHint("(arrow keys or highlighted letter, Esc to go back)")
                        .AddOption("C", "Create new feed", "create")
                        .AddOption("U", "Update feed metadata", "update")
                        .AddOption("R", "Rename feed ID", "rename")
                        .AddOption("D", "Delete feed", "delete")
                        .AddOption("I", "Set icon", "icon-set")
                        .AddOption("X", "Remove icon", "icon-remove")
                        .AllowCancel()
                        .Show();

                    switch (manageChoice)
                    {
                        case "create":
                            // Prompt for feed details
                            var id = AnsiConsole.Ask<string>("Feed [bold]ID[/] (URL-friendly slug):");
                            var title = AnsiConsole.Ask<string>("Feed [bold]title[/]:");
                            var author = AnsiConsole.Ask<string>("Feed [bold]author[/]:");
                            var description = AnsiConsole.Ask("Description (optional):", string.Empty);
                            var summary = AnsiConsole.Ask("Summary (optional, defaults to description):", string.Empty);
                            var email = AnsiConsole.Ask("Email (optional):", string.Empty);
                            var language = AnsiConsole.Ask("Language:", "en");
                            var category = AnsiConsole.Ask("Category (optional):", string.Empty);

                            var feedConfig = new FeedConfig
                            {
                                Id = id.Trim(),
                                Title = title.Trim(),
                                Description = string.IsNullOrEmpty(description) ? null : description.Trim(),
                                Summary = string.IsNullOrEmpty(summary) ? null : summary.Trim(),
                                Author = author.Trim(),
                                Email = string.IsNullOrEmpty(email) ? null : email.Trim(),
                                Language = language.Trim(),
                                Category = string.IsNullOrEmpty(category) ? null : category.Trim()
                            };

                            var createResult = await FeedCreateCommand.CreateFeedAsync(httpClient, feedConfig, cancellationToken: cancellationToken);
                            if (createResult.Success)
                            {
                                currentFeed = createResult.Feed;
                                AnsiConsole.MarkupLine($"Switched to feed: [cyan]{Markup.Escape(currentFeed!.Title)}[/]");
                            }
                            WaitForKeyPress();
                            break;

                        case "update":
                            var feedToUpdate = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                            if (feedToUpdate != null)
                            {
                                await FeedUpdateCommand.UpdateFeedInteractiveAsync(httpClient, feedToUpdate, cancellationToken);
                                // Refresh current feed if it was updated
                                if (currentFeed?.Id == feedToUpdate.Id)
                                {
                                    currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedToUpdate.Id);
                                }
                                WaitForKeyPress();
                            }
                            break;

                        case "rename":
                            var feedToRename = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                            if (feedToRename != null)
                            {
                                var newId = AnsiConsole.Ask<string>("New feed [bold]ID[/]:");
                                var renameResult = await FeedRenameCommand.RenameFeedAsync(httpClient, feedToRename.Id, newId.Trim(), cancellationToken);
                                if (renameResult.Success && currentFeed?.Id == renameResult.OldFeedId)
                                {
                                    // Follow the rename
                                    currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, renameResult.FeedId!);
                                    AnsiConsole.MarkupLine($"Switched to renamed feed: [cyan]{Markup.Escape(currentFeed?.Title ?? renameResult.FeedId!)}[/]");
                                }
                                WaitForKeyPress();
                            }
                            break;

                        case "delete":
                            var feedToDelete = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                            if (feedToDelete != null)
                            {
                                var deleteResult = await FeedDeleteCommand.DeleteFeedAsync(httpClient, feedToDelete.Id, cancellationToken: cancellationToken);
                                if (deleteResult.Success && currentFeed?.Id == deleteResult.FeedId)
                                {
                                    // Current feed was deleted, select a new one
                                    currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, contextMessage: "Previous feed was deleted.", currentUser: currentUser);
                                }
                                WaitForKeyPress();
                            }
                            break;

                        case "icon-set":
                            var feedForIcon = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                            if (feedForIcon == null)
                            {
                                WaitForKeyPress();
                                break;
                            }
                            currentFeed = feedForIcon;

                            // Prompt for icon path
                            var iconPath = AnsiConsole.Prompt(
                                new TextPrompt<string>("Enter icon file path [grey](PNG or JPEG)[/]:")
                                    .AllowEmpty());

                            if (string.IsNullOrWhiteSpace(iconPath))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            // Clean up path
                            iconPath = iconPath.Trim().Trim('"', '\'');

                            // Validate file exists
                            if (!File.Exists(iconPath))
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] File not found: {Markup.Escape(iconPath)}");
                                WaitForKeyPress();
                                break;
                            }

                            // Validate extension
                            var iconExt = Path.GetExtension(iconPath).ToLowerInvariant();
                            if (iconExt != ".png" && iconExt != ".jpg" && iconExt != ".jpeg")
                            {
                                AnsiConsole.MarkupLine("[red]✗[/] Icon must be a PNG or JPEG file");
                                WaitForKeyPress();
                                break;
                            }

                            // Upload icon
                            await FeedHelpers.UploadIconAsync(httpClient, feedForIcon.Id, iconPath);
                            WaitForKeyPress();
                            break;

                        case "icon-remove":
                            var feedToRemoveIcon = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, currentUser: currentUser);
                            if (feedToRemoveIcon == null)
                            {
                                WaitForKeyPress();
                                break;
                            }
                            currentFeed = feedToRemoveIcon;

                            // Confirm removal
                            var confirmRemove = new MenuBuilder<bool?>()
                                .WithTitle($"Remove icon from feed [cyan]{Markup.Escape(feedToRemoveIcon.Title)}[/]?")
                                .WithHint("(Y/N, Esc to cancel)")
                                .AddOption("Y", "Yes", true)
                                .AddOption("N", "No", false)
                                .AllowCancel(true, false)
                                .Show();

                            if (confirmRemove != true)
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            AnsiConsole.WriteLine();

                            // Delete icon
                            await FeedHelpers.DeleteIconAsync(httpClient, feedToRemoveIcon.Id);
                            WaitForKeyPress();
                            break;
                    }
                    break;

                case MenuChoice.SwitchEnvironment:
                    var newEnv = EnvironmentHelpers.SelectEnvironment();
                    if (newEnv != null && newEnv != env)
                    {
                        env = newEnv;
                        autoConnect = PreferencesHelpers.GetAutoConnectEnabled(env) ?? true;
                        apiUrl = EnvironmentHelpers.BuildConfiguration(env)["Api:BaseUrl"]?.TrimEnd('/') + "/api";

                        // Reset and reconnect for new environment
                        currentFeed = null;
                        (httpClient, currentUser, isConnected, serverVersion) = await ShowHeader(
                            env, apiUrl, currentFeed, shouldConnect: autoConnect);

                        if (isConnected && httpClient != null)
                        {
                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, currentUser: currentUser);
                        }
                        else if (!autoConnect)
                        {
                            AnsiConsole.MarkupLine("[yellow]Auto-connect disabled. Use Settings to connect manually.[/]");
                            WaitForKeyPress();
                        }
                    }
                    break;

                case MenuChoice.Preferences:
                    var preferencesChoice = new MenuBuilder<string?>()
                        .WithTitle("Settings:")
                        .WithHint("(arrow keys or highlighted letter, Esc to go back)")
                        .AddOption("A", "Auto-connect on startup", "autoconnect")
                        .AddOption("C", "Connect now", "connect")
                        .AddOption("N", "Audio normalization", "normalization")
                        .AddOption("K", "Update API key (local)", "apikey-local")
                        .AddOption("R", "Rotate API key (server)", "apikey-rotate")
                        .AddOption("S", "Show all preferences", "show-all")
                        .AddOption("G", "Generate config files", "generate")
                        .AllowCancel()
                        .Show();

                    switch (preferencesChoice)
                    {
                        case "autoconnect":
                            var currentAutoConnect = PreferencesHelpers.GetAutoConnectEnabled(env) ?? true;
                            var autoConnectChoice = new MenuBuilder<bool?>()
                                .WithTitle($"Auto-connect on startup is currently {(currentAutoConnect ? "enabled" : "disabled")}:")
                                .WithHint("(arrow keys or E/D, Esc to cancel)")
                                .AddOption("E", "Enable auto-connect", true)
                                .AddOption("D", "Disable auto-connect", false)
                                .AllowCancel()
                                .Show();

                            if (autoConnectChoice.HasValue)
                            {
                                PreferencesHelpers.SetAutoConnectEnabled(env, autoConnectChoice.Value);
                                AnsiConsole.MarkupLine($"[green]✓[/] Auto-connect on startup {(autoConnectChoice.Value ? "enabled" : "disabled")}");

                                // If enabling and not connected, offer to connect now
                                if (autoConnectChoice.Value && !isConnected)
                                {
                                    AnsiConsole.WriteLine();
                                    if (await AnsiConsole.ConfirmAsync("Connect now?", defaultValue: true, cancellationToken: cancellationToken))
                                    {
                                        (httpClient, currentUser, isConnected, serverVersion) = await ShowHeader(
                                            env, apiUrl, currentFeed, shouldConnect: true);

                                        if (isConnected && httpClient != null)
                                        {
                                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, currentUser: currentUser);
                                        }
                                    }
                                }
                            }
                            break;

                        case "connect":
                            if (isConnected)
                            {
                                AnsiConsole.MarkupLine("[green]✓[/] Already connected.");
                                WaitForKeyPress();
                            }
                            else
                            {
                                (httpClient, currentUser, isConnected, serverVersion) = await ShowHeader(
                                    env, apiUrl, currentFeed, shouldConnect: true);

                                if (isConnected && httpClient != null)
                                {
                                    currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, currentUser: currentUser);
                                }
                            }
                            break;

                        case "normalization":
                            var currentNorm = PreferencesHelpers.GetNormalizationEnabled(env) ?? true;
                            var normChoice = new MenuBuilder<bool?>()
                                .WithTitle($"Audio normalization is currently {(currentNorm ? "enabled" : "disabled")}:")
                                .WithHint("(arrow keys or E/D, Esc to cancel)")
                                .AddOption("E", "Enable normalization", true)
                                .AddOption("D", "Disable normalization", false)
                                .AllowCancel()
                                .Show();

                            if (normChoice.HasValue)
                            {
                                PreferencesHelpers.SetNormalizationEnabled(env, normChoice.Value);
                                AnsiConsole.MarkupLine($"[green]✓[/] Audio normalization {(normChoice.Value ? "enabled" : "disabled")}");
                                WaitForKeyPress();
                            }
                            break;

                        case "apikey-local":
                            var currentKey = PreferencesHelpers.GetApiKey(env);

                            AnsiConsole.MarkupLine(!string.IsNullOrEmpty(currentKey)
                                ? $"Current API key: [cyan]{PreferencesHelpers.MaskApiKey(currentKey)}[/]"
                                : "[yellow]No API key currently configured.[/]");

                            AnsiConsole.WriteLine();

                            var newApiKey = AnsiConsole.Prompt(new TextPrompt<string>("Enter new API key (or press Enter to cancel):").AllowEmpty());

                            if (!string.IsNullOrWhiteSpace(newApiKey))
                            {
                                PreferencesHelpers.SaveApiKey(env, newApiKey.Trim());
                                AnsiConsole.MarkupLine($"[green]✓[/] API key saved for {env}");

                                // Offer to reconnect with new key
                                AnsiConsole.WriteLine();
                                if (await AnsiConsole.ConfirmAsync("Reconnect with new API key?", true, cancellationToken))
                                {
                                    (httpClient, currentUser, isConnected, serverVersion) = await ShowHeader(
                                        env, apiUrl, currentFeed, shouldConnect: true);

                                    if (isConnected && httpClient != null)
                                    {
                                        currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, currentUser: currentUser);
                                    }
                                    else
                                    {
                                        currentFeed = null;
                                    }
                                }
                                else
                                {
                                    WaitForKeyPress();
                                }
                            }
                            break;

                        case "apikey-rotate":
                            if (!isConnected || httpClient == null)
                            {
                                AnsiConsole.MarkupLine("[yellow]Not connected. Connect first to rotate your API key on the server.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            // Get current user ID from /api/me
                            try
                            {
                                var meResponse = await httpClient.GetAsync("/api/users/me", cancellationToken);
                                if (!meResponse.IsSuccessStatusCode)
                                {
                                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to get current user: {meResponse.StatusCode}");
                                    WaitForKeyPress();
                                    break;
                                }

                                var meJson = await meResponse.Content.ReadAsStringAsync(cancellationToken);
                                var meData = JsonSerializer.Deserialize<JsonElement>(meJson);

                                if (!meData.TryGetProperty("id", out var idElement))
                                {
                                    AnsiConsole.MarkupLine("[red]✗[/] Could not determine current user ID");
                                    WaitForKeyPress();
                                    break;
                                }

                                var userId = idElement.GetString();
                                AnsiConsole.MarkupLine($"Current user: [cyan]{Markup.Escape(userId ?? "")}[/]");
                                AnsiConsole.WriteLine();

                                var confirmRotate = await AnsiConsole.ConfirmAsync("Are you sure you want to rotate your API key? The current key will stop working.", false, cancellationToken);
                                if (!confirmRotate)
                                {
                                    AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                    WaitForKeyPress();
                                    break;
                                }

                                var rotateResponse = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId!)}/key/regenerate", null, cancellationToken);

                                if (rotateResponse.IsSuccessStatusCode)
                                {
                                    var rotateJson = await rotateResponse.Content.ReadAsStringAsync(cancellationToken);
                                    var rotateData = JsonSerializer.Deserialize<JsonElement>(rotateJson);

                                    AnsiConsole.MarkupLine("[green]✓[/] API key rotated successfully");
                                    AnsiConsole.WriteLine();

                                    if (rotateData.TryGetProperty("apiKey", out var apiKeyElement))
                                    {
                                        var rotatedKey = apiKeyElement.GetString();
                                        AnsiConsole.MarkupLine($"[yellow bold]New API Key:[/] [cyan]{Markup.Escape(rotatedKey ?? "")}[/]");
                                        AnsiConsole.WriteLine();

                                        // Save and reconnect
                                        var saveRotatedKey = await AnsiConsole.ConfirmAsync($"Save this key and reconnect?", true, cancellationToken);
                                        if (saveRotatedKey && !string.IsNullOrEmpty(rotatedKey))
                                        {
                                            PreferencesHelpers.SaveApiKey(env, rotatedKey);
                                            AnsiConsole.MarkupLine($"[green]✓[/] API key saved for {env}");

                                            // Reconnect with new key
                                            (httpClient, currentUser, isConnected, serverVersion) = await ShowHeader(
                                                env, apiUrl, currentFeed, shouldConnect: true);

                                            if (isConnected && httpClient != null)
                                            {
                                                currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, currentUser: currentUser);
                                            }
                                            else
                                            {
                                                currentFeed = null;
                                            }
                                        }
                                        else
                                        {
                                            AnsiConsole.MarkupLine("[yellow]Δ[/] API key was NOT saved. Copy it now - it will NOT be shown again!");
                                            AnsiConsole.MarkupLine("[yellow]You will need to manually update your API key to reconnect.[/]");
                                            isConnected = false;
                                            httpClient = null;
                                            currentUser = null;
                                            currentFeed = null;
                                            WaitForKeyPress();
                                        }
                                    }
                                }
                                else
                                {
                                    var errorContent = await rotateResponse.Content.ReadAsStringAsync(cancellationToken);
                                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to rotate API key: {rotateResponse.StatusCode}");
                                    if (!string.IsNullOrEmpty(errorContent))
                                    {
                                        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(errorContent)}");
                                    }
                                    WaitForKeyPress();
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Error rotating API key: {ex.Message}");
                                WaitForKeyPress();
                            }
                            break;

                        case "show-all":
                            var showApiKey = PreferencesHelpers.GetApiKey(env);
                            var showFilePath = PreferencesHelpers.GetPreferencesPath();

                            AnsiConsole.MarkupLine(string.IsNullOrEmpty(showApiKey)
                                ? $"[yellow]API key ({env}):[/] (not configured)"
                                : $"[bold]API key ({env}):[/] {PreferencesHelpers.MaskApiKey(showApiKey)}");

                            var showNormPref = PreferencesHelpers.GetNormalizationEnabled(env);
                            var showNormEnabled = showNormPref ?? true;
                            AnsiConsole.MarkupLine($"[bold]Audio normalization ({env}):[/] {(showNormEnabled ? "enabled" : "disabled")}{(showNormPref.HasValue ? "" : " (default)")}");

                            var showAutoConnectPref = PreferencesHelpers.GetAutoConnectEnabled(env);
                            var showAutoConnectEnabled = showAutoConnectPref ?? true;
                            AnsiConsole.MarkupLine($"[bold]Auto-connect ({env}):[/] {(showAutoConnectEnabled ? "enabled" : "disabled")}{(showAutoConnectPref.HasValue ? "" : " (default)")}");

                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine($"[grey]Preferences: {Markup.Escape(showFilePath)}[/]");
                            WaitForKeyPress();
                            break;

                        case "generate":
                            var configFiles = new[] { "appsettings.json", "appsettings.Dev.json", "appsettings.Test.json", "appsettings.Prod.json" };

                            var selectedFiles = AnsiConsole.Prompt(
                                new MultiSelectionPrompt<string>()
                                    .Title("Select configuration files to generate:")
                                    .NotRequired()
                                    .PageSize(10)
                                    .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                                    .AddChoices(configFiles));

                            if (selectedFiles.Count == 0)
                            {
                                AnsiConsole.MarkupLine("[grey]No files selected.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var outputPath = Directory.GetCurrentDirectory();
                            var genAssembly = Assembly.GetExecutingAssembly();
                            var generatedCount = 0;

                            foreach (var fileName in selectedFiles)
                            {
                                var resourceName = $"FeatherPod.{fileName}";
                                var targetPath = Path.Combine(outputPath, fileName);

                                if (File.Exists(targetPath))
                                {
                                    var overwrite = await AnsiConsole.ConfirmAsync($"[yellow]{fileName}[/] already exists. Overwrite?", defaultValue: false, cancellationToken: cancellationToken);
                                    if (!overwrite)
                                    {
                                        AnsiConsole.MarkupLine($"[grey]Skipped {fileName}[/]");
                                        continue;
                                    }
                                }

                                await using var stream = genAssembly.GetManifestResourceStream(resourceName);
                                if (stream == null)
                                {
                                    AnsiConsole.MarkupLine($"[red]✗[/] Could not find embedded resource: {resourceName}");
                                    continue;
                                }

                                using var reader = new StreamReader(stream);
                                var content = await reader.ReadToEndAsync(cancellationToken);
                                await File.WriteAllTextAsync(targetPath, content, cancellationToken);

                                AnsiConsole.MarkupLine($"[green]✓[/] Generated [cyan]{fileName}[/]");
                                generatedCount++;
                            }

                            if (generatedCount > 0)
                            {
                                AnsiConsole.WriteLine();
                                AnsiConsole.MarkupLine($"Generated {generatedCount} file(s) to [cyan]{outputPath}[/]");
                            }
                            WaitForKeyPress();
                            break;
                    }
                    break;

                case MenuChoice.Quit:
                    AnsiConsole.MarkupLine("[grey]Bye.[/]");
                    AnsiConsole.WriteLine();
                    return 0;
            }
        }
    }

    private static void WaitForKeyPress()
    {
        AnsiConsole.Markup("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    private static async Task<(HttpClient?, CurrentUserInfo?, bool isConnected, string? serverVersion)> ShowHeader(
        string env,
        string? apiUrl,
        FeedConfig? currentFeed,
        bool shouldConnect,
        bool currentlyConnected = false,
        string? existingServerVersion = null)
    {
        Console.Write("\e[2J\e[H");
        AnsiConsole.WriteLine();

        // Get CLI version
        var versionAttr = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = versionAttr?.InformationalVersion ?? "unknown";

        // Show title with server version if available
        AnsiConsole.MarkupLine(!string.IsNullOrEmpty(existingServerVersion)
            ? $"[bold]FeatherPod Episode Manager[/] [grey]v{version} (server: {existingServerVersion})[/]"
            : $"[bold]FeatherPod Episode Manager[/] [grey]v{version}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Environment: [cyan]{env}[/]");
        AnsiConsole.WriteLine();

        if (!string.IsNullOrEmpty(apiUrl))
        {
            AnsiConsole.MarkupLine($"API: [cyan]{apiUrl}[/]");
            if (!shouldConnect)
            {
                AnsiConsole.WriteLine();
            }
        }

        HttpClient? httpClient = null;
        CurrentUserInfo? userInfo = null;
        var isConnected = currentlyConnected;
        var serverVersion = existingServerVersion;

        if (shouldConnect)
        {
            // Get API key
            var apiKey = PreferencesHelpers.GetApiKey(env);
            if (string.IsNullOrEmpty(apiKey))
            {
                if (!PreferencesHelpers.PromptAndSaveApiKey(env))
                {
                    AnsiConsole.MarkupLine("[red]✗[/] Disconnected (no API key)");
                    AnsiConsole.WriteLine();
                    ShowFeedStatus(currentFeed, false);
                    return (null, null, false, null);
                }
                apiKey = PreferencesHelpers.GetApiKey(env);
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                AnsiConsole.MarkupLine("[red]✗[/] Disconnected (no API key)");
                AnsiConsole.WriteLine();
                ShowFeedStatus(currentFeed, false);
                return (null, null, false, null);
            }

            // Create HttpClient
            var configuration = EnvironmentHelpers.BuildConfiguration(env);
            var apiBaseUrl = configuration["Api:BaseUrl"] ?? "";
            httpClient = new() { BaseAddress = new(apiBaseUrl) };
            httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);

            // Test connection with spinner
            try
            {
                await AnsiConsole.Status()
                    .StartAsync("Connecting...", async _ =>
                    {
                        // Fetch user info
                        var response = await httpClient.GetAsync("/api/users/me");
                        response.EnsureSuccessStatusCode();

                        var json = await response.Content.ReadAsStringAsync();
                        var userData = JsonSerializer.Deserialize<JsonElement>(json);

                        var id = userData.GetProperty("id").GetString() ?? "";
                        var role = userData.GetProperty("role").GetString() ?? "FeedOwner";
                        var ownedFeeds = new List<string>();

                        if (userData.TryGetProperty("ownedFeeds", out var feedsElement) && feedsElement.ValueKind == JsonValueKind.Array)
                        {
                            ownedFeeds = feedsElement.EnumerateArray()
                                .Select(e => e.GetString() ?? "")
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();
                        }

                        userInfo = new(id, role, ownedFeeds);

                        // Fetch server version
                        try
                        {
                            var versionResponse = await httpClient.GetAsync("/api/version");
                            if (versionResponse.IsSuccessStatusCode)
                            {
                                var versionJson = await versionResponse.Content.ReadAsStringAsync();
                                var versionData = JsonSerializer.Deserialize<JsonElement>(versionJson);
                                if (versionData.TryGetProperty("version", out var serverVer))
                                {
                                    serverVersion = serverVer.GetString();
                                }
                            }
                        }
                        catch
                        {
                            // Ignore version fetch errors
                        }
                    });

                isConnected = true;
                Console.WriteLine();
                AnsiConsole.MarkupLine("[green]✓[/] Connected");

                // Update title line with server version immediately
                if (!string.IsNullOrEmpty(serverVersion))
                {
                    Console.Write($"\e[s\e[2;1H\e[2K");
                    AnsiConsole.Markup($"[bold]FeatherPod Episode Manager[/] [grey]v{version} (server: {serverVersion})[/]");
                    Console.Write("\e[u");
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine("[red]✗[/] Authentication failed (invalid API key)");
                httpClient = null;
                isConnected = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]✗[/] Connection failed: {Markup.Escape(ex.Message)}");
                httpClient = null;
                isConnected = false;
            }
        }
        else
        {
            AnsiConsole.MarkupLine(isConnected ? "[green]✓[/] Connected" : "[red]✗[/] Disconnected");
        }

        AnsiConsole.WriteLine();
        ShowFeedStatus(currentFeed, isConnected);

        return (httpClient, userInfo, isConnected, serverVersion);
    }

    private static void ShowFeedStatus(FeedConfig? currentFeed, bool isConnected)
    {
        if (currentFeed != null)
        {
            AnsiConsole.MarkupLine($"Feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
        }
        else if (!isConnected)
        {
            AnsiConsole.MarkupLine("[grey]No feed selected (not connected)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No feed selected[/]");
        }

        AnsiConsole.WriteLine();
    }

    private static MenuChoice ShowMenu(FeedConfig? currentFeed, bool isConnected, CurrentUserInfo? currentUser)
    {
        var menu = new MenuBuilder<MenuChoice>()
            .WithTitle("What would you like to do?")
            .WithHint("(arrow keys or highlighted letter)")
            .AddOption("L", "List episodes", MenuChoice.List)
            .AddOption("P", "Push episodes", MenuChoice.Push)
            .AddOption("D", "Delete episodes", MenuChoice.Delete)
            .AddOption("V", "Move/Copy episodes", MenuChoice.MoveCopy);

        // Only show User Management for Admin users
        if (currentUser?.Role == "Admin")
        {
            menu.AddOption("U", "User management", MenuChoice.UserManagement);
        }

        return menu
            .AddOption("M", "Manage feeds", MenuChoice.ManageFeeds)
            .AddOption("F", "Switch feed", MenuChoice.SwitchFeed)
            .AddOption("E", "Environment", MenuChoice.SwitchEnvironment)
            .AddOption("S", "Settings", MenuChoice.Preferences)
            .AddOption("Q", "Quit", MenuChoice.Quit)
            .AllowCancel(false) // Don't allow escape on main menu
            .Show();
    }

    private enum MenuChoice
    {
        List,
        Push,
        Delete,
        MoveCopy,
        UserManagement,
        SwitchFeed,
        ManageFeeds,
        Preferences,
        SwitchEnvironment,
        Quit
    }
}
