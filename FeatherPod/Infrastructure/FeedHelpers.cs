using System.Net.Http.Headers;
using System.Text.Json;
using FeatherPod.Shared.Models;
using Spectre.Console;

using static FeatherPod.Infrastructure.ConsoleWriter;

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
            Out.Error($"Error fetching feeds: {ex.Message}");

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
            Out.Error($"Error fetching feed: {ex.Message}");

            return null;
        }
    }

    internal static async Task<FeedConfig?> SelectFeedAsync(HttpClient httpClient, string environment, bool forcePrompt = false, string? contextMessage = null, CurrentUserInfo? currentUser = null, bool showNoFeedsMessage = true)
    {
        var feeds = await GetFeedsAsync(httpClient);

        // Filter feeds by ownership for FeedOwner users
        if (currentUser?.Role == "FeedOwner")
        {
            feeds = feeds.Where(f => currentUser.OwnedFeeds.Contains(f.Id)).ToList();
        }

        if (feeds.Count == 0)
        {
            if (showNoFeedsMessage)
            {
                var message = currentUser?.Role == "FeedOwner"
                    ? "[yellow]No feeds available.[/] You don't own any feeds."
                    : "[yellow]No feeds found.[/] Use [cyan]'M: Manage Feeds'[/] to create one.";
                Out.MarkupLine(message);
                Out.BlankLine();
            }

            return null;
        }

        // Check last-used first (before single-feed shortcut)
        var lastUsed = PreferencesHelpers.GetLastSelectedFeed(environment);
        var lastUsedFeed = string.IsNullOrEmpty(lastUsed) ? null : feeds.FirstOrDefault(f => string.Equals(f.Id, lastUsed, StringComparison.OrdinalIgnoreCase));

        // Auto-select if not forcing prompt
        if (!forcePrompt)
        {
            // Return last-used if valid
            if (lastUsedFeed != null)
            {
                return lastUsedFeed;
            }

            // Return single feed if only one exists
            if (feeds.Count == 1)
            {
                PreferencesHelpers.SetLastSelectedFeed(environment, feeds[0].Id);

                return feeds[0];
            }
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
            PreferencesHelpers.SetLastSelectedFeed(environment, selected.Id);
        }

        return selected;
    }

    internal static async Task<bool> UploadIconAsync(HttpClient httpClient, string feedId, string iconPath)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            Out.MarkupLine($"[grey]Uploading icon to: {Markup.Escape(url)}[/]");
            Out.MarkupLine($"[grey]Base URL: {Markup.Escape(httpClient.BaseAddress?.ToString() ?? "null")}[/]");

            await using var fileStream = File.OpenRead(iconPath);
            using var formData = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            formData.Add(fileContent, "file", Path.GetFileName(iconPath));

            var response = await httpClient.PostAsync(url, formData);

            Out.MarkupLine($"[grey]Response status: {response.StatusCode}[/]");

            if (response.IsSuccessStatusCode)
            {
                Out.Success("Uploaded icon");

                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Error($"Failed to upload icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(errorContent);
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error uploading icon: {ex.Message}");

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
                Out.Success("Removed icon");

                return true;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Warning(errorContent);

                return false;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Error($"Failed to delete icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(errorContent);
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error deleting icon: {ex.Message}");

            return false;
        }
    }

}
