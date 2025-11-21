using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;

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
        // Clear screen before any output
        Console.Write("\e[2J\e[H");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment, useDefault: true);
        if (env == null) return 1;

        HttpClient? httpClient = null;
        CurrentUserInfo? currentUser = null;

        var isConnected = false;
        var autoConnect = PreferencesHelpers.GetAutoConnectEnabled() ?? true;

        if (autoConnect)
        {
            var (client, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
            if (client != null)
            {
                httpClient = client;
                currentUser = userInfo;
                isConnected = true;
            }
            else
            {
                // Connection failed, continue in disconnected mode
                AnsiConsole.MarkupLine("[yellow]Continuing in disconnected mode. Use Preferences to configure API key or change auto-connect.[/]");
                AnsiConsole.WriteLine();
            }
        }
        else
        {
            // Auto-connect disabled
            var configuration = EnvironmentHelpers.BuildConfiguration(env);
            var apiBaseUrl = configuration["Api:BaseUrl"] ?? "unknown";
            AnsiConsole.MarkupLine($"API: [cyan]{apiBaseUrl}/api[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Auto-connect disabled. Use Preferences to connect manually.[/]");
            AnsiConsole.WriteLine();
        }

        // Select initial feed (only if connected)
        FeedConfig? currentFeed = null;
        if (isConnected && httpClient != null)
        {
            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
            if (currentFeed == null)
            {
                AnsiConsole.MarkupLine("[yellow]No feeds available. Create one using 'M: Manage Feeds'.[/]");
                AnsiConsole.WriteLine();
            }
        }

        // Skip clear on first iteration (header already shown from setup)
        var skipClear = true;

        // Main menu loop
        while (true)
        {
            if (!skipClear)
            {
                // Clear screen and redraw header
                ShowHeader(env);
                if (httpClient != null)
                {
                    AnsiConsole.MarkupLine($"API: [cyan]{httpClient.BaseAddress}[/]");
                }
                else
                {
                    var configuration = EnvironmentHelpers.BuildConfiguration(env);
                    var apiBaseUrl = configuration["Api:BaseUrl"] ?? "unknown";
                    AnsiConsole.MarkupLine($"API: [cyan]{apiBaseUrl}[/]");
                }
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(isConnected ? "[green]✓[/] Connected" : "[red]✗[/] Disconnected");
                AnsiConsole.WriteLine();
            }
            skipClear = false;

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
                    var pushFeed = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                    if (pushFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feeds available. Create one using 'M: Manage Feeds'.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    // Prompt for file path(s)
                    var filePattern = AnsiConsole.Prompt(
                        new TextPrompt<string>("Enter file path(s) [grey](supports wildcards like *.mp3, comma-separated)[/]:")
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
                        AnsiConsole.MarkupLine($"[red]Error:[/] No files found matching pattern: {Markup.Escape(filePattern)}");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.MarkupLine($"Found [cyan]{filesToUpload.Count}[/] file(s)");
                    AnsiConsole.WriteLine();

                    // Confirm files
                    var fileListDisplay = filesToUpload.Count <= 5
                        ? string.Join(", ", filesToUpload.Select(f => $"[cyan]{Markup.Escape(Path.GetFileName(f))}[/]"))
                        : $"[cyan]{filesToUpload.Count}[/] files";

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

                    // Prompt for normalization (default to user preference)
                    var currentNormPref = PreferencesHelpers.GetNormalizationEnabled() ?? true;
                    var normalizeChoice = new MenuBuilder<bool?>()
                        .WithTitle($"Normalize audio to -16 LUFS? [grey](current preference: {(currentNormPref ? "enabled" : "disabled")})[/]")
                        .WithHint("(Y/N, Enter for default)")
                        .AddOption("Y", "Yes - Normalize", true)
                        .AddOption("N", "No - Keep original", false)
                        .AllowCancel(true, currentNormPref)
                        .Show();

                    if (normalizeChoice == null)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    AnsiConsole.WriteLine();

                    // Temporarily set normalization preference for upload
                    var originalNormPref = PreferencesHelpers.GetNormalizationEnabled();
                    PreferencesHelpers.SetNormalizationEnabled(normalizeChoice.Value);

                    try
                    {
                        var configuration = EnvironmentHelpers.BuildConfiguration(env);
                        var uploadSettings = new PushSettings
                        {
                            Files = filePattern,
                            Title = string.IsNullOrWhiteSpace(pushTitle) ? null : pushTitle.Trim(),
                            Description = string.IsNullOrWhiteSpace(pushDescription) ? null : pushDescription.Trim(),
                            Summary = string.IsNullOrWhiteSpace(pushSummary) ? null : pushSummary.Trim(),
                            ExtractDateFromFile = dateSource
                        };

                        var successCount = 0;
                        var failureCount = 0;

                        foreach (var file in filesToUpload)
                        {
                            var success = await EpisodeHelpers.UploadEpisodeAsync(httpClient, configuration, pushFeed, file, uploadSettings);
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
                            PreferencesHelpers.SetNormalizationEnabled(originalNormPref.Value);
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
                    var sourceFeed = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, contextMessage: "Select source feed:");
                    if (sourceFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feeds available.[/]");
                        WaitForKeyPress();
                        break;
                    }

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
                        AnsiConsole.MarkupLine("[red]Error:[/] No target feeds available.");
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
                        AnsiConsole.MarkupLine("[red]Error:[/] Cannot copy episodes within the same feed.");
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

                case MenuChoice.Icon:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Settings to connect.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    var iconChoice = new MenuBuilder<string?>()
                        .WithTitle("Icon management:")
                        .WithHint("(S/R, Esc to go back)")
                        .AddOption("S", "Set icon", "set")
                        .AddOption("R", "Remove icon", "remove")
                        .AllowCancel()
                        .Show();

                    if (iconChoice == null)
                        break;

                    // Select feed for icon operation
                    var iconFeed = currentFeed ?? await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                    if (iconFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feeds available.[/]");
                        WaitForKeyPress();
                        break;
                    }

                    if (iconChoice == "set")
                    {
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
                            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(iconPath)}");
                            WaitForKeyPress();
                            break;
                        }

                        // Validate extension
                        var ext = Path.GetExtension(iconPath).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                        {
                            AnsiConsole.MarkupLine("[red]Error:[/] Icon must be a PNG or JPEG file");
                            WaitForKeyPress();
                            break;
                        }

                        // Upload icon
                        await FeedHelpers.UploadIconAsync(httpClient, iconFeed.Id, iconPath);
                    }
                    else // remove
                    {
                        // Confirm removal
                        var confirmRemove = new MenuBuilder<bool?>()
                            .WithTitle($"Remove icon from feed [cyan]{Markup.Escape(iconFeed.Title)}[/]?")
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
                        await FeedHelpers.DeleteIconAsync(httpClient, iconFeed.Id);
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
                                        table.AddColumn("[cyan]User ID[/]");
                                        table.AddColumn("[cyan]Name[/]");
                                        table.AddColumn("[cyan]Role[/]");
                                        table.AddColumn("[cyan]Owned Feeds[/]");

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
                                                role == "Admin" ? "[green]Admin[/]" : "[cyan]FeedOwner[/]",
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
                            var newUserId = AnsiConsole.Prompt(new TextPrompt<string>("User [cyan]ID[/]:").AllowEmpty());
                            if (string.IsNullOrWhiteSpace(newUserId))
                            {
                                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                                WaitForKeyPress();
                                break;
                            }

                            var newName = AnsiConsole.Ask<string>("Display [cyan]name[/]:");
                            var newEmail = AnsiConsole.Ask<string>("[cyan]Email[/]:");

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
                                    email = newEmail.Trim(),
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
                                        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
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
                        WaitForKeyPress();
                    }
                    else if (currentFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feed selected. Use 'M: Manage Feeds' to create one.[/]");
                        WaitForKeyPress();
                    }
                    else
                    {
                        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, currentFeed.Id);
                        if (episodes == null || episodes.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]No episodes to delete.[/]");
                            WaitForKeyPress();
                        }
                        else
                        {
                            var selected = EpisodeHelpers.SelectEpisodesMulti(episodes);
                            if (selected.Count == 1)
                            {
                                await EpisodeDeleteCommand.DeleteEpisodeAsync(httpClient, currentFeed.Id, selected[0], cancellationToken: cancellationToken);
                                WaitForKeyPress();
                            }
                            else if (selected.Count > 1)
                            {
                                AnsiConsole.MarkupLine("[yellow]Multiple episodes selected. Delete one at a time in interactive mode.[/]");
                                WaitForKeyPress();
                            }
                        }
                    }
                    break;

                case MenuChoice.SwitchFeed:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Preferences to connect.[/]");
                        WaitForKeyPress();
                    }
                    else
                    {
                        var newFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                        if (newFeed != null)
                        {
                            currentFeed = newFeed;
                        }
                    }
                    // No pause - feed title shows in menu header
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
                        .WithHint("(arrow keys or C/U/R/D, Esc to go back)")
                        .AddOption("C", "Create new feed", "create")
                        .AddOption("U", "Update feed metadata", "update")
                        .AddOption("R", "Rename feed ID", "rename")
                        .AddOption("D", "Delete feed", "delete")
                        .AllowCancel()
                        .Show();

                    switch (manageChoice)
                    {
                        case "create":
                            // Prompt for feed details
                            var id = AnsiConsole.Ask<string>("Feed [cyan]ID[/] (URL-friendly slug):");
                            var title = AnsiConsole.Ask<string>("Feed [cyan]title[/]:");
                            var author = AnsiConsole.Ask<string>("Feed [cyan]author[/]:");
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
                            var feedToUpdate = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
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
                            var feedToRename = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                            if (feedToRename != null)
                            {
                                var newId = AnsiConsole.Ask<string>("New feed [cyan]ID[/]:");
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
                            var feedToDelete = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                            if (feedToDelete != null)
                            {
                                var deleteResult = await FeedDeleteCommand.DeleteFeedAsync(httpClient, feedToDelete.Id, cancellationToken: cancellationToken);
                                if (deleteResult.Success && currentFeed?.Id == deleteResult.FeedId)
                                {
                                    // Current feed was deleted, select a new one
                                    currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, contextMessage: "Previous feed was deleted.");
                                }
                                WaitForKeyPress();
                            }
                            break;
                    }
                    break;

                case MenuChoice.SwitchEnvironment:
                    var newEnv = EnvironmentHelpers.SelectEnvironment();
                    if (newEnv != null && newEnv != env)
                    {
                        env = newEnv;
                        autoConnect = PreferencesHelpers.GetAutoConnectEnabled() ?? true;

                        // Clear screen and show connection progress
                        ShowHeader(env);

                        if (autoConnect)
                        {
                            var (newClient, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                            if (newClient != null)
                            {
                                httpClient = newClient;
                                currentUser = userInfo;
                                isConnected = true;
                                // Select feed for new environment
                                currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                            }
                            else
                            {
                                httpClient = null;
                                currentUser = null;
                                isConnected = false;
                                currentFeed = null;
                            }
                        }
                        else
                        {
                            httpClient = null;
                            currentUser = null;
                            isConnected = false;
                            currentFeed = null;
                            var configuration = EnvironmentHelpers.BuildConfiguration(env);
                            var apiBaseUrl = configuration["Api:BaseUrl"] ?? "unknown";
                            AnsiConsole.MarkupLine($"API: [cyan]{apiBaseUrl}/api[/]");
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]Auto-connect disabled. Use Preferences to connect manually.[/]");
                            AnsiConsole.WriteLine();
                        }

                        // Skip clear on next iteration (header already shown)
                        skipClear = true;
                    }
                    break;

                case MenuChoice.Preferences:
                    var preferencesChoice = new MenuBuilder<string?>()
                        .WithTitle("Preferences:")
                        .WithHint("(arrow keys or A/C/N/K/R, Esc to go back)")
                        .AddOption("A", "Auto-connect on startup", "autoconnect")
                        .AddOption("C", "Connect now", "connect")
                        .AddOption("N", "Audio normalization", "normalization")
                        .AddOption("K", "Update API key (local)", "apikey-local")
                        .AddOption("R", "Rotate API key (server)", "apikey-rotate")
                        .AllowCancel()
                        .Show();

                    switch (preferencesChoice)
                    {
                        case "autoconnect":
                            var currentAutoConnect = PreferencesHelpers.GetAutoConnectEnabled() ?? true;
                            var autoConnectChoice = new MenuBuilder<bool?>()
                                .WithTitle($"Auto-connect on startup is currently {(currentAutoConnect ? "enabled" : "disabled")}:")
                                .WithHint("(arrow keys or E/D, Esc to cancel)")
                                .AddOption("E", "Enable auto-connect", true)
                                .AddOption("D", "Disable auto-connect", false)
                                .AllowCancel()
                                .Show();

                            if (autoConnectChoice.HasValue)
                            {
                                PreferencesHelpers.SetAutoConnectEnabled(autoConnectChoice.Value);
                                AnsiConsole.MarkupLine($"[green]✓[/] Auto-connect on startup {(autoConnectChoice.Value ? "enabled" : "disabled")}");

                                // If enabling and not connected, offer to connect now
                                if (autoConnectChoice.Value && !isConnected)
                                {
                                    AnsiConsole.WriteLine();
                                    if (await AnsiConsole.ConfirmAsync("Connect now?", defaultValue: true, cancellationToken: cancellationToken))
                                    {
                                        // Clear screen and show connection progress like at startup
                                        ShowHeader(env);

                                        var (newClient, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                                        if (newClient != null)
                                        {
                                            httpClient = newClient;
                                            currentUser = userInfo;
                                            isConnected = true;
                                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                                        }
                                        skipClear = true;
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
                                // Clear screen and show connection progress like at startup
                                ShowHeader(env);

                                var (newClient, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                                if (newClient != null)
                                {
                                    httpClient = newClient;
                                    currentUser = userInfo;
                                    isConnected = true;
                                    currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                                }
                                skipClear = true;
                            }
                            break;

                        case "normalization":
                            var currentNorm = PreferencesHelpers.GetNormalizationEnabled() ?? true;
                            var normChoice = new MenuBuilder<bool?>()
                                .WithTitle($"Audio normalization is currently {(currentNorm ? "enabled" : "disabled")}:")
                                .WithHint("(arrow keys or E/D, Esc to cancel)")
                                .AddOption("E", "Enable normalization", true)
                                .AddOption("D", "Disable normalization", false)
                                .AllowCancel()
                                .Show();

                            if (normChoice.HasValue)
                            {
                                PreferencesHelpers.SetNormalizationEnabled(normChoice.Value);
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
                                    ShowHeader(env);
                                    var (newClient, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                                    if (newClient != null)
                                    {
                                        httpClient = newClient;
                                        currentUser = userInfo;
                                        isConnected = true;
                                        currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                                    }
                                    else
                                    {
                                        currentUser = null;
                                        isConnected = false;
                                        currentFeed = null;
                                    }
                                    skipClear = true;
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
                                    AnsiConsole.MarkupLine($"[red]Error:[/] Failed to get current user: {meResponse.StatusCode}");
                                    WaitForKeyPress();
                                    break;
                                }

                                var meJson = await meResponse.Content.ReadAsStringAsync(cancellationToken);
                                var meData = JsonSerializer.Deserialize<JsonElement>(meJson);

                                if (!meData.TryGetProperty("id", out var idElement))
                                {
                                    AnsiConsole.MarkupLine("[red]Error:[/] Could not determine current user ID");
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
                                            ShowHeader(env);
                                            var (newClient, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                                            if (newClient != null)
                                            {
                                                httpClient = newClient;
                                                currentUser = userInfo;
                                                isConnected = true;
                                                currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                                            }
                                            else
                                            {
                                                currentUser = null;
                                                isConnected = false;
                                                currentFeed = null;
                                            }
                                            skipClear = true;
                                        }
                                        else
                                        {
                                            AnsiConsole.MarkupLine("[yellow]Warning:[/] API key was NOT saved. Copy it now - it will NOT be shown again!");
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
                                        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
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
        AnsiConsole.WriteLine();
        AnsiConsole.Markup("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    private static void ShowHeader(string env)
    {
        Console.Write("\e[2J\e[H");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Environment: [cyan]{env}[/]");
        AnsiConsole.WriteLine();
    }

    private static MenuChoice ShowMenu(FeedConfig? currentFeed, bool isConnected, CurrentUserInfo? currentUser)
    {
        if (currentFeed != null)
        {
            AnsiConsole.MarkupLine($"Feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
            AnsiConsole.WriteLine();
        }
        else if (!isConnected)
        {
            AnsiConsole.MarkupLine("[grey]No feed selected (not connected)[/]");
            AnsiConsole.WriteLine();
        }

        var menu = new MenuBuilder<MenuChoice>()
            .WithTitle("What would you like to do?")
            .WithHint("(arrow keys or highlighted letter)")
            .AddOption("L", "List episodes", MenuChoice.List)
            .AddOption("P", "Push episodes", MenuChoice.Push)
            .AddOption("D", "Delete episodes", MenuChoice.Delete)
            .AddOption("O", "Move/Copy episodes", MenuChoice.MoveCopy)
            .AddOption("I", "Icon management", MenuChoice.Icon);

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
        Icon,
        UserManagement,
        SwitchFeed,
        ManageFeeds,
        Preferences,
        SwitchEnvironment,
        Quit
    }
}
