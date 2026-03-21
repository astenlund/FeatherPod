namespace FeatherPod.Server.Services;

public class FakeAiService : IAiService
{
    public bool IsAvailable => true;

    public Task<string?> SuggestTitleAsync(string filename, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(EpisodeService.ParseTitleFromFilename(filename));
    }
}
