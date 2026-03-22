using FeatherPod.Shared.Services;

namespace FeatherPod.Server.Services;

public class FakeAiService : IAiService
{
    public bool IsAvailable => true;

    public Task<string?> SuggestTitleAsync(string filename, string? note = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(EpisodeService.ParseTitleFromFilename(filename));
    }
}
