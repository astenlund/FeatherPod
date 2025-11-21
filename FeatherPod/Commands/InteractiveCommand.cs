using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings;
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

            var choice = ShowMenu(currentFeed, isConnected);

            switch (choice)
            {
                case MenuChoice.List:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Preferences to connect.[/]");
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

                case MenuChoice.Delete:
                    if (!isConnected || httpClient == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Not connected. Use Preferences to connect.[/]");
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

    private static MenuChoice ShowMenu(FeedConfig? currentFeed, bool isConnected)
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

        return new MenuBuilder<MenuChoice>()
            .WithTitle("What would you like to do?")
            .WithHint("(arrow keys or highlighted letter)")
            .AddOption("L", "List episodes", MenuChoice.List)
            .AddOption("D", "Delete episode", MenuChoice.Delete)
            .AddOption("M", "Manage feeds", MenuChoice.ManageFeeds)
            .AddOption("F", "Switch feed", MenuChoice.SwitchFeed)
            .AddOption("E", "Environment", MenuChoice.SwitchEnvironment)
            .AddOption("P", "Preferences", MenuChoice.Preferences)
            .AddOption("Q", "Quit", MenuChoice.Quit)
            .AllowCancel(false) // Don't allow escape on main menu
            .Show();
    }

    private enum MenuChoice
    {
        List,
        Delete,
        SwitchFeed,
        ManageFeeds,
        Preferences,
        SwitchEnvironment,
        Quit
    }
}
