using System.Net.Http.Headers;
using System.Text.Json;
using FeatherPod.Shared.Models;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

internal static class FeedHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<List<FeedConfig>> GetFeedsAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/feeds");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<FeedConfig>>(json, JsonOptions) ?? [];
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching feeds:[/] {ex.Message}");
            return [];
        }
    }

    internal static async Task<FeedConfig?> GetFeedByIdAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feedId}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<FeedConfig>(json, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching feed:[/] {ex.Message}");
            return null;
        }
    }

    internal static async Task<FeedConfig?> SelectFeedAsync(HttpClient httpClient, string environment, bool forcePrompt = false, string? contextMessage = null)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds found.[/] Use [cyan]'M: Manage Feeds'[/] to create one.");
            AnsiConsole.WriteLine();
            return null;
        }

        if (feeds.Count == 1 && !forcePrompt)
        {
            return feeds[0];
        }

        // Multiple feeds - check last-used
        var lastUsed = UserSettings.Default.GetLastUsedFeed(environment);
        var lastUsedFeed = string.IsNullOrEmpty(lastUsed) ? null : feeds.FirstOrDefault(f => f.Id == lastUsed);

        if (!forcePrompt && lastUsedFeed != null)
        {
            return lastUsedFeed;
        }

        // Show feed selector with optional context message
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle($"{contextMessage} Select feed:".Trim())
            .WithHint("(arrow keys, Enter to select)")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(
                null,
                $"[cyan]{Markup.Escape(feed.Title)}[/]",
                feed
            );
        }

        var selected = menu.Show();
        if (selected != null)
        {
            UserSettings.Default.SetLastUsedFeed(environment, selected.Id);
        }

        return selected;
    }

    internal static async Task<FeedConfig?> CreateFeedAsync(HttpClient httpClient)
    {
        var id = AnsiConsole.Ask<string>("Feed ID (URL-friendly slug):");
        var title = AnsiConsole.Ask<string>("Title:");
        var description = AnsiConsole.Ask("Description (optional):", string.Empty);
        var author = AnsiConsole.Ask<string>("Author:");
        var email = AnsiConsole.Ask("Email (optional):", string.Empty);
        var language = AnsiConsole.Ask("Language:", "en");
        var category = AnsiConsole.Ask("Category (optional):", string.Empty);
        var iconPath = AnsiConsole.Ask("Icon path (optional, PNG/JPEG):", string.Empty);
        iconPath = iconPath.Trim().Trim('"', '\''); // Remove surrounding quotes

        var feedConfig = new FeedConfig
        {
            Id = id,
            Title = title,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Author = author,
            Email = string.IsNullOrEmpty(email) ? null : email,
            Language = language,
            Category = string.IsNullOrEmpty(category) ? null : category
        };

        try
        {
            // Create the feed first
            var json = JsonSerializer.Serialize(feedConfig, JsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("/api/feeds", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed to create feed:[/] {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
                AnsiConsole.WriteLine();
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var createdFeed = JsonSerializer.Deserialize<FeedConfig>(responseContent, JsonOptions);

            AnsiConsole.MarkupLine($"[green]✓[/] Created feed: [cyan]{Markup.Escape(id)}[/]");

            // Upload icon if provided
            if (!string.IsNullOrEmpty(iconPath))
            {
                if (!File.Exists(iconPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Icon file not found: {Markup.Escape(iconPath)}");
                }
                else
                {
                    await UploadIconAsync(httpClient, id, iconPath);
                }
            }

            AnsiConsole.WriteLine();
            return createdFeed;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating feed:[/] {ex.Message}");
            AnsiConsole.WriteLine();
            return null;
        }
    }

    internal static async Task<bool> UploadIconAsync(HttpClient httpClient, string feedId, string iconPath)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            AnsiConsole.MarkupLine($"[grey]Uploading icon to: {Markup.Escape(url)}[/]");
            AnsiConsole.MarkupLine($"[grey]Base URL: {Markup.Escape(httpClient.BaseAddress?.ToString() ?? "null")}[/]");

            await using var fileStream = File.OpenRead(iconPath);
            using var formData = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            formData.Add(fileContent, "file", Path.GetFileName(iconPath));

            var response = await httpClient.PostAsync(url, formData);

            AnsiConsole.MarkupLine($"[grey]Response status: {response.StatusCode}[/]");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Uploaded icon");
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to upload icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error uploading icon: {ex.Message}");
            return false;
        }
    }

    internal static async Task<bool> DeleteIconAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            var response = await httpClient.DeleteAsync(url);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Removed icon");
                return true;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[yellow]⚠[/] {errorContent}");
                return false;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting icon: {ex.Message}");
            return false;
        }
    }

    internal static async Task<(bool wasRenamed, string? oldId, FeedConfig? renamedFeed)> RenameFeedAsync(HttpClient httpClient)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds to rename.[/]");
            AnsiConsole.WriteLine();
            return (false, null, null);
        }

        // Select feed to rename
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle("Select feed to rename:")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(null, $"[cyan]{Markup.Escape(feed.Id)}[/] - {Markup.Escape(feed.Title)}", feed);
        }

        var selectedFeed = menu.Show();
        if (selectedFeed == null)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            AnsiConsole.WriteLine();
            return (false, null, null);
        }

        var oldId = selectedFeed.Id;
        var newId = AnsiConsole.Ask<string>("New feed ID:");

        try
        {
            var response = await httpClient.PostAsync($"/api/feeds/{selectedFeed.Id}/rename?newId={Uri.EscapeDataString(newId)}", null);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Renamed feed from [cyan]{Markup.Escape(selectedFeed.Id)}[/] to [cyan]{Markup.Escape(newId)}[/]");
                AnsiConsole.WriteLine();

                // Fetch the renamed feed
                var updatedFeed = await GetFeedByIdAsync(httpClient, newId);
                return (true, oldId, updatedFeed);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed to rename feed:[/] {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error renaming feed:[/] {ex.Message}");
        }

        AnsiConsole.WriteLine();
        return (false, null, null);
    }

    internal static async Task<(bool wasDeleted, string? deletedFeedId)> DeleteFeedAsync(HttpClient httpClient)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds to delete.[/]");
            AnsiConsole.WriteLine();
            return (false, null);
        }

        // Select feed to delete
        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle("Select feed to [red]delete[/]:")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(null, $"[cyan]{Markup.Escape(feed.Id)}[/] - {Markup.Escape(feed.Title)}", feed);
        }

        var selectedFeed = menu.Show();
        if (selectedFeed == null)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            AnsiConsole.WriteLine();
            return (false, null);
        }

        // Confirm deletion
        var confirmed = new MenuBuilder<bool?>()
            .WithTitle($"[red]Delete feed[/] [cyan]{Markup.Escape(selectedFeed.Id)}[/] and all its episodes?")
            .WithHint("(arrow keys or Y/N, Esc to cancel)")
            .AddOption("Y", "Yes", true)
            .AddOption("N", "No", false)
            .AllowCancel(true, false)
            .Show();

        if (confirmed != true)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            AnsiConsole.WriteLine();

            return (false, null);
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{selectedFeed.Id}");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted feed: [cyan]{Markup.Escape(selectedFeed.Id)}[/]");
                AnsiConsole.WriteLine();

                return (true, selectedFeed.Id);
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            AnsiConsole.MarkupLine($"[red]Failed to delete feed:[/] {response.StatusCode}");
            if (!string.IsNullOrEmpty(errorContent))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {errorContent}");
            }
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error deleting feed:[/] {ex.Message}");
        }

        AnsiConsole.WriteLine();
        return (false, null);
    }

    internal static async Task<FeedManagementResult> ManageFeedsAsync(HttpClient httpClient)
    {
        while (true)
        {
            var choice = new MenuBuilder<string?>()
                .WithTitle("Manage Feeds:")
                .WithHint("(arrow keys or C/R/D, Esc to go back)")
                .AddOption("C", "Create new feed", "create")
                .AddOption("R", "Rename feed", "rename")
                .AddOption("D", "Delete feed", "delete")
                .AllowCancel()
                .Show();

            if (choice == null) break;

            switch (choice)
            {
                case "create":
                    var newFeed = await CreateFeedAsync(httpClient);
                    return new() { CreatedFeed = newFeed };
                case "rename":
                    var (wasRenamed, oldId, renamedFeed) = await RenameFeedAsync(httpClient);
                    if (wasRenamed)
                    {
                        return new() { RenamedFeed = renamedFeed, OldFeedId = oldId };
                    }
                    break;
                case "delete":
                    var (wasDeleted, deletedFeedId) = await DeleteFeedAsync(httpClient);
                    if (wasDeleted)
                    {
                        return new() { DeletedFeedId = deletedFeedId };
                    }
                    break;
            }
        }

        return new(); // User cancelled (pressed Esc)
    }

    internal record FeedManagementResult
    {
        public FeedConfig? CreatedFeed { get; init; }
        public FeedConfig? RenamedFeed { get; init; }
        public string? OldFeedId { get; init; }
        public string? DeletedFeedId { get; init; }
    }
}
