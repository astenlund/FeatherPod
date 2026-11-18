using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Move, copy and delete against concurrent writers of the same episode id or file name: an upload's normalization writes the audio
/// path outside the episode lock at any time before its join publishes the episode, and a join publishes under the same id.
/// </summary>
[Collection("Sequential")]
public sealed class EpisodeBlobOwnershipTests : IDisposable
{
    private const string SourceFeed = "own-source";
    private const string TargetFeed = "own-target";
    private const string FileName = "show.mp3";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FeatherPodOwnership_{Guid.NewGuid():N}");
    private readonly HookedBlobService _blob;
    private readonly EpisodeService _episodes;

    public EpisodeBlobOwnershipTests()
    {
        Directory.CreateDirectory(_root);
        _blob = new HookedBlobService(_root);
        _episodes = new EpisodeService(_blob, NullLogger<EpisodeService>.Instance);
    }

    public void Dispose()
    {
        _episodes.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the test's temp tree
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_TargetHasEpisodeWithTheFileName_IsRefusedAndChangesNothing(bool move)
    {
        // Arrange: the target already has a different episode under the same file name, with its own audio
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        var existing = await PublishUploadAsync(TargetFeed, [9, 9, 9], DateTime.UtcNow.AddMinutes(-5), "WEBVTT\n\nexisting");

        // Act
        var exception = await Record.ExceptionAsync(() => TransferAsync(move, source.Id));

        // Assert
        Assert.Equal(EpisodeConflictReason.TargetHasEpisode, Assert.IsType<EpisodeConflictException>(exception).Reason);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id));
        Assert.Equal(existing, Assert.Single(await _episodes.GetAllEpisodesAsync(TargetFeed)));
        Assert.Equal([9, 9, 9], ReadAudio(TargetFeed));
        Assert.Equal([1, 2, 3], ReadAudio(SourceFeed));
        Assert.Equal("WEBVTT\n\nexisting", await ReadTranscriptAsync(TargetFeed, existing.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_TargetHasEpisodeWithTheFileNameButNoBlob_IsRefusedBeforeCopyingAnyAudio(bool move)
    {
        // Arrange: the target's same-name episode has lost its audio blob, so only the episode check can refuse before the copy
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        await PublishUploadAsync(TargetFeed, [9, 9, 9], DateTime.UtcNow.AddMinutes(-5), null);
        File.Delete(AudioPath(TargetFeed));

        // Act
        var exception = await Record.ExceptionAsync(() => TransferAsync(move, source.Id));

        // Assert: refused without leaving a copied blob behind
        Assert.Equal(EpisodeConflictReason.TargetHasEpisode, Assert.IsType<EpisodeConflictException>(exception).Reason);
        Assert.False(File.Exists(AudioPath(TargetFeed)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_TargetPathHoldsAnUnjoinedUploadsAudio_IsRefusedWithoutOverwritingIt(bool move)
    {
        // Arrange: an upload's normalization has written the target path; its join has not published it yet
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        WriteAudio(TargetFeed, [7, 7, 7]);

        // Act
        var exception = await Record.ExceptionAsync(() => TransferAsync(move, source.Id));

        // Assert
        Assert.Equal(EpisodeConflictReason.TargetPathTaken, Assert.IsType<EpisodeConflictException>(exception).Reason);
        Assert.Equal([7, 7, 7], ReadAudio(TargetFeed));
        Assert.Empty(await _episodes.GetAllEpisodesAsync(TargetFeed));
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id));
        Assert.Equal([1, 2, 3], ReadAudio(SourceFeed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_UploadPublishesTheFileNameToTheTargetDuringTheCopy_IsRefusedAtCommit(bool move)
    {
        // Arrange: after the transfer's create-only copy, an upload's normalization overwrites the target path and its join publishes
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        Episode? upload = null;
        _blob.AfterCreateAudio = () => upload = PublishUploadAsync(TargetFeed, [7, 7, 7], DateTime.UtcNow, "WEBVTT\n\nupload").GetAwaiter().GetResult();

        // Act
        var exception = await Record.ExceptionAsync(() => TransferAsync(move, source.Id));

        // Assert: the source is untouched and the target holds only the upload, with its own audio and transcript
        Assert.Equal(EpisodeConflictReason.TargetHasEpisode, Assert.IsType<EpisodeConflictException>(exception).Reason);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id));
        Assert.Equal([1, 2, 3], ReadAudio(SourceFeed));
        Assert.Equal(upload, Assert.Single(await _episodes.GetAllEpisodesAsync(TargetFeed)));
        Assert.Equal([7, 7, 7], ReadAudio(TargetFeed));
        Assert.Equal("WEBVTT\n\nupload", await ReadTranscriptAsync(TargetFeed, upload!.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_SameFileReuploadedDuringTheTransfer_PublishesNothingAndKeepsTheReupload(bool move)
    {
        // Arrange: during the blob I/O the same file is re-uploaded: its normalization overwrites the source audio and its join
        // replaces the episode under the same id
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        Episode? reupload = null;
        _blob.AfterDownloadAudio = () => reupload = PublishUploadAsync(SourceFeed, [5, 5, 5], DateTime.UtcNow.AddSeconds(1), null).GetAwaiter().GetResult();

        // Act
        var exception = await Record.ExceptionAsync(() => TransferAsync(move, source.Id));

        // Assert: refused because the incarnation was replaced, nothing published to the target, and the re-upload keeps its episode
        // and its audio
        Assert.Contains("was deleted or replaced", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.Empty(await _episodes.GetAllEpisodesAsync(TargetFeed));
        Assert.Equal(reupload, await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id));
        Assert.Equal([5, 5, 5], ReadAudio(SourceFeed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_SourceAudioReplacedWithoutAJoin_TransfersAndKeepsTheReplacement(bool move)
    {
        // Arrange: a same-name upload's normalization overwrote the source audio, and its job then ended without a join (cancelled
        // or failed), so nothing will ever record the new blob's ETag on the episode
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        WriteAudio(SourceFeed, [5, 5, 5]);

        // Act
        var transferred = await TransferAsync(move, source.Id);

        // Assert: the transfer carries the bytes the episode serves, and the replaced source blob is not the episode's to delete
        Assert.Equal(transferred, Assert.Single(await _episodes.GetAllEpisodesAsync(TargetFeed)));
        Assert.Equal([5, 5, 5], ReadAudio(TargetFeed));
        Assert.Equal([5, 5, 5], ReadAudio(SourceFeed));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Transfer_TargetFeedRenamedOrDeletedDuringTheCopy_IsRefusedAndKeepsTheSource(bool move, bool rename)
    {
        // Arrange: after the transfer's audio copy, the target feed is renamed away or deleted
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        _blob.AfterCreateAudio = () =>
        {
            var change = rename ? _episodes.RenameFeedAsync(TargetFeed, "own-target-renamed") : _episodes.DeleteFeedAsync(TargetFeed);
            change.GetAwaiter().GetResult();
        };

        // Act
        var exception = await Record.ExceptionAsync(() => TransferAsync(move, source.Id));

        // Assert: refused because the target feed is gone, the source episode and its audio kept, and nothing saved under the old id
        Assert.Contains($"Target feed '{TargetFeed}' not found", Assert.IsType<InvalidOperationException>(exception).Message);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id));
        Assert.Equal([1, 2, 3], ReadAudio(SourceFeed));
        Assert.False(File.Exists(Path.Combine(_root, TargetFeed, "episodes.json")));
    }

    [Fact]
    public async Task Move_ReuploadWritesTheSourceAudioAfterTheCopy_KeepsTheReuploadsAudio()
    {
        // Arrange: after the move copied the audio, a re-upload's normalization overwrites the source path (its join still pending)
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        _blob.AfterCreateAudio = () => WriteAudio(SourceFeed, [5, 5, 5]);

        // Act
        var moved = await _episodes.MoveEpisodeAsync(source.Id, SourceFeed, TargetFeed);

        // Assert: the move published the copied audio, and the source delete left the re-upload's audio for its join
        Assert.Equal(moved, Assert.Single(await _episodes.GetAllEpisodesAsync(TargetFeed)));
        Assert.Equal([1, 2, 3], ReadAudio(TargetFeed));
        Assert.Empty(await _episodes.GetAllEpisodesAsync(SourceFeed));
        Assert.Equal([5, 5, 5], ReadAudio(SourceFeed));
    }

    [Fact]
    public async Task Move_EpisodeWithoutRecordedOwnership_KeepsAReuploadsAudioWrittenAfterTheDownload()
    {
        // Arrange: an episode stored before ownership was recorded; after the move copied its audio, a re-upload's normalization
        // overwrites the source path (its join still pending)
        var source = await SeedSourceAsync();
        await _episodes.AddEpisodeFromEntityAsync(source with { AudioETag = null });
        await CreateFeedAsync(TargetFeed);
        _blob.AfterCreateAudio = () => WriteAudio(SourceFeed, [5, 5, 5]);

        // Act
        await _episodes.MoveEpisodeAsync(source.Id, SourceFeed, TargetFeed);

        // Assert: the source delete is conditional on the version the move downloaded, so the re-upload's audio stays
        Assert.Equal([1, 2, 3], ReadAudio(TargetFeed));
        Assert.Empty(await _episodes.GetAllEpisodesAsync(SourceFeed));
        Assert.Equal([5, 5, 5], ReadAudio(SourceFeed));
    }

    [Fact]
    public async Task Move_SourceAudioSharedWithAnotherEpisode_IsKept()
    {
        // Arrange: another source episode serves the same file name (a same-name upload of a different size)
        var source = await SeedSourceAsync();
        await _episodes.AddEpisodeFromEntityAsync(source with { Id = "other-id", UploadedAt = DateTime.UtcNow.AddMinutes(-1) });
        await CreateFeedAsync(TargetFeed);

        // Act
        await _episodes.MoveEpisodeAsync(source.Id, SourceFeed, TargetFeed);

        // Assert
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(SourceFeed, "other-id"));
        Assert.Equal([1, 2, 3], ReadAudio(SourceFeed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_RecordsTheTargetCopysOwnership_SoADeleteRemovesIt(bool move)
    {
        // Arrange
        var source = await SeedSourceAsync();
        await CreateFeedAsync(TargetFeed);
        var transferred = await TransferAsync(move, source.Id);

        // Act
        var deleted = await _episodes.DeleteEpisodeAsync(TargetFeed, transferred.Id);

        // Assert: the delete removed the target copy; a move had removed its source audio, a copy keeps it
        Assert.True(deleted);
        Assert.NotNull(transferred.AudioETag);
        Assert.False(File.Exists(AudioPath(TargetFeed)));
        Assert.Equal(!move, File.Exists(AudioPath(SourceFeed)));
    }

    [Fact]
    public async Task DirectUpload_SameNameAsAnOlderEpisode_RecordsItsBlobOnTheSibling_SoTheLastDeleteRemovesTheAudio()
    {
        // Arrange: two direct uploads of the same file name with different sizes share one blob, the newer upload's
        await CreateFeedAsync(SourceFeed);
        var file = Path.Combine(_root, FileName);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var older = await _episodes.AddEpisodeAsync(SourceFeed, file);
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var newer = await _episodes.AddEpisodeAsync(SourceFeed, file);

        // Act: the newer episode is deleted first (the blob is still shared), then the older one
        await _episodes.DeleteEpisodeAsync(SourceFeed, newer.Id);
        await _episodes.DeleteEpisodeAsync(SourceFeed, older.Id);

        // Assert: the older episode owned the newer upload's blob, so its delete removed it
        Assert.NotEqual(older.Id, newer.Id);
        Assert.False(File.Exists(AudioPath(SourceFeed)));
    }

    [Fact]
    public async Task Delete_ReuploadsAudioWrittenBeforeTheDelete_IsKept()
    {
        // Arrange: a re-upload's normalization has overwritten the episode's audio; its join has not run yet
        var source = await SeedSourceAsync();
        WriteAudio(SourceFeed, [5, 5, 5]);

        // Act
        var deleted = await _episodes.DeleteEpisodeAsync(SourceFeed, source.Id);

        // Assert: the episode is gone, but the re-upload's audio stays for its join to publish
        Assert.True(deleted);
        Assert.Null(await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id));
        Assert.Equal([5, 5, 5], ReadAudio(SourceFeed));
    }

    [Fact]
    public async Task Delete_OwnedAudio_IsRemoved()
    {
        // Arrange
        var source = await SeedSourceAsync();

        // Act
        await _episodes.DeleteEpisodeAsync(SourceFeed, source.Id);

        // Assert
        Assert.False(File.Exists(AudioPath(SourceFeed)));
    }

    [Fact]
    public async Task Delete_EpisodeWithoutRecordedOwnership_RemovesTheAudioUnconditionally()
    {
        // Arrange: an episode stored before ownership was recorded, whose audio was since rewritten
        var source = await SeedSourceAsync();
        await _episodes.AddEpisodeFromEntityAsync(source with { AudioETag = null });
        WriteAudio(SourceFeed, [5, 5, 5]);

        // Act
        await _episodes.DeleteEpisodeAsync(SourceFeed, source.Id);

        // Assert
        Assert.False(File.Exists(AudioPath(SourceFeed)));
    }

    [Fact]
    public async Task Delete_ReuploadPublishesItsTranscriptAroundTheDelete_KeepsTheReuploadsTranscript()
    {
        // Arrange: when the delete removes the transcript, a re-upload of the same file publishes the same id with its own transcript
        // (on another task, so a delete that removes the transcript inside the episode lock makes it wait)
        var source = await SeedSourceAsync(TranscriptStatus.Available, "WEBVTT\n\nsource");
        Task? reupload = null;
        _blob.OnDeleteTranscript = () =>
        {
            reupload = Task.Run(() => PublishUploadAsync(SourceFeed, [1, 2, 3], DateTime.UtcNow.AddSeconds(1), "WEBVTT\n\nreuploaded"));

            // A delete outside the lock lets the re-upload land first; one that holds the lock makes it wait
            reupload.Wait(TimeSpan.FromMilliseconds(500));
        };

        // Act
        await _episodes.DeleteEpisodeAsync(SourceFeed, source.Id);
        await reupload!;

        // Assert
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(SourceFeed, source.Id))?.TranscriptStatus);
        Assert.Equal("WEBVTT\n\nreuploaded", await ReadTranscriptAsync(SourceFeed, source.Id));
    }

    [Fact]
    public async Task RenameFeed_RecordsTheMovedBlobsOwnership_SoADeleteRemovesTheAudio()
    {
        // Arrange: a direct upload records its blob's ETag; the rename moves the blob to a new version
        await CreateFeedAsync(SourceFeed);
        var file = Path.Combine(_root, FileName);
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var uploaded = await _episodes.AddEpisodeAsync(SourceFeed, file);
        await _episodes.RenameFeedAsync(SourceFeed, "own-renamed");

        // Act: the hourly sync (or a restart) reloads the feed from storage before the delete
        await _episodes.SyncWithBlobStorageAsync("own-renamed");
        var reloaded = await _episodes.GetEpisodeByIdAsync("own-renamed", uploaded.Id);
        await _episodes.DeleteEpisodeAsync("own-renamed", uploaded.Id);

        // Assert: the rename persisted the moved blob's ETag and the new feed id
        Assert.NotNull(uploaded.AudioETag);
        Assert.Equal("own-renamed", reloaded?.FeedId);
        Assert.False(File.Exists(AudioPath("own-renamed")));
    }

    [Fact]
    public async Task UploadJoin_RecordsTheNormalizedBlobsETag_OnTheEpisodeAndItsSameNameSiblings()
    {
        // Arrange: a same-name episode of another size already serves the path; the Function reports the ETag of its new upload
        var jobs = new InMemoryJobService();
        var completion = new JobCompletionService(jobs, _episodes, _blob, new NullJobProgressChannel(), new NullFeedEventChannel(), new StubPushNotificationService(new StubBlobService(), _episodes, jobs), NullLogger<JobCompletionService>.Instance);
        var sibling = await SeedSourceAsync();
        jobs.SetEntity(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = "job-etag",
            Status = nameof(JobStatus.Processing),
            FeedId = SourceFeed,
            FileName = FileName,
            Title = "Upload",
            Source = nameof(UploadSource.Browser),
            OriginalFileSize = 3,
            QueuedAt = DateTimeOffset.UtcNow
        });

        // Act
        await completion.HandleNormalizationCompleteAsync("job-etag", new NormalizationCompleteRequest { Success = true, NormalizedFileSize = 4, AudioETag = "\"normalized\"" }, CancellationToken.None);

        // Assert
        var episodeId = jobs.GetEntity("job-etag")!.EpisodeId!;
        Assert.Equal("\"normalized\"", (await _episodes.GetEpisodeByIdAsync(SourceFeed, episodeId))?.AudioETag);
        Assert.Equal("\"normalized\"", (await _episodes.GetEpisodeByIdAsync(SourceFeed, sibling.Id))?.AudioETag);
    }

    [Fact]
    public void ResetForNormalizationRetry_ClearsTheRecordedAudioETag()
    {
        // Arrange
        var job = new JobStatusEntity { PartitionKey = JobStorageNames.JobsPartitionKey, RowKey = "job-reset", Status = nameof(JobStatus.Failed), AudioETag = "\"old\"" };

        // Act
        JobRetryService.ResetForNormalizationRetry(job, speechAvailable: false, DateTimeOffset.UtcNow);

        // Assert
        Assert.Null(job.AudioETag);
    }

    private Task<Episode> TransferAsync(bool move, string episodeId) =>
        move ? _episodes.MoveEpisodeAsync(episodeId, SourceFeed, TargetFeed) : _episodes.CopyEpisodeAsync(episodeId, SourceFeed, TargetFeed);

    private async Task CreateFeedAsync(string feedId) =>
        await _episodes.CreateFeedAsync(new FeedConfig { Id = feedId, Title = feedId, Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });

    /// <summary>
    /// A published source episode owning its audio [1, 2, 3], as an upload's join leaves it.
    /// </summary>
    private async Task<Episode> SeedSourceAsync(TranscriptStatus? transcriptStatus = null, string? transcript = null)
    {
        await CreateFeedAsync(SourceFeed);

        return await PublishUploadAsync(SourceFeed, [1, 2, 3], DateTime.UtcNow.AddMinutes(-10), transcript, transcriptStatus);
    }

    /// <summary>
    /// Simulate an upload of <see cref="FileName"/> to a feed: its normalization writes the audio path outside the episode lock, then
    /// its join publishes the episode with that blob's ETag (and its transcript, when given).
    /// </summary>
    private async Task<Episode> PublishUploadAsync(string feedId, byte[] audio, DateTime uploadedAt, string? transcript, TranscriptStatus? transcriptStatus = null)
    {
        WriteAudio(feedId, audio);
        var episode = new Episode
        {
            Id = Episode.GenerateId(feedId, FileName, audio.Length),
            FeedId = feedId,
            Title = "Show",
            FileName = FileName,
            FileSize = audio.Length,
            Source = UploadSource.Browser,
            UploadedAt = uploadedAt,
            TranscriptStatus = transcript != null ? TranscriptStatus.Available : transcriptStatus,
            AudioETag = _blob.GetAudioETag(feedId, FileName)
        };

        return await _episodes.AddEpisodeFromEntityAsync(episode, CancellationToken.None, transcript);
    }

    private string AudioPath(string feedId) => Path.Combine(_root, feedId, "audio", FileName);

    private void WriteAudio(string feedId, byte[] audio)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AudioPath(feedId))!);
        File.WriteAllBytes(AudioPath(feedId), audio);
    }

    private byte[] ReadAudio(string feedId) => File.ReadAllBytes(AudioPath(feedId));

    private async Task<string?> ReadTranscriptAsync(string feedId, string episodeId)
    {
        await using var stream = await _blob.DownloadTranscriptAsync(feedId, episodeId);

        return stream == null ? null : await new StreamReader(stream).ReadToEndAsync();
    }

    private sealed class HookedBlobService(string root) : TestBlobStorageService(root)
    {
        /// <summary>
        /// Runs once, right after the next audio download to a temp file.
        /// </summary>
        public Action? AfterDownloadAudio { get; set; }

        /// <summary>
        /// Runs once, right after the next successful create-only audio upload.
        /// </summary>
        public Action? AfterCreateAudio { get; set; }

        /// <summary>
        /// Runs once, just before the next transcript delete.
        /// </summary>
        public Action? OnDeleteTranscript { get; set; }

        public override async Task<AudioDownload> DownloadAudioToTempAsync(string feedId, string fileName)
        {
            var download = await base.DownloadAudioToTempAsync(feedId, fileName);
            TakeOnce(() => AfterDownloadAudio, value => AfterDownloadAudio = value)?.Invoke();

            return download;
        }

        public override async Task<string?> CreateAudioAsync(string feedId, string fileName, string filePath)
        {
            var eTag = await base.CreateAudioAsync(feedId, fileName, filePath);
            if (eTag != null)
            {
                TakeOnce(() => AfterCreateAudio, value => AfterCreateAudio = value)?.Invoke();
            }

            return eTag;
        }

        public override Task DeleteTranscriptAsync(string feedId, string episodeId)
        {
            TakeOnce(() => OnDeleteTranscript, value => OnDeleteTranscript = value)?.Invoke();

            return base.DeleteTranscriptAsync(feedId, episodeId);
        }

        private static Action? TakeOnce(Func<Action?> get, Action<Action?> set)
        {
            var hook = get();
            set(null);

            return hook;
        }
    }
}
