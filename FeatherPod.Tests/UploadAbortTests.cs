using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using FeatherPod.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatherPod.Tests;

/// <summary>
/// Async-normalization uploads whose request is aborted, and the client upload ID the job carries so that a client can
/// recognize a job whose upload response it never received.
/// </summary>
[Collection("IntegrationTests")]
public sealed class UploadAbortTests : IDisposable
{
    private const string TestFeedId = "abort-feed";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // Disposing the base factory also disposes the factories WithWebHostBuilder derives from it
    private readonly FeatherPodWebApplicationFactory _baseFactory = new();

    public void Dispose()
    {
        _baseFactory.Dispose();
    }

    [AzuriteFact]
    public async Task UploadEpisode_CancelsJobWithoutQueueingIt_WhenRequestIsAbortedAfterJobEntityIsCreated()
    {
        // Arrange
        var jobService = new RecordingJobService { AbortOnCreate = true };
        var uploads = new UploadRequestTracker();
        using var factory = CreateFactory(jobService, uploads);
        using var client = factory.CreateClient();
        await CreateTestFeedAsync(client);
        using var request = CreateUploadRequest("aborted.mp3", clientUploadId: "0d6f1e3a-5a52-4f4b-9a3e-2f6a1c9b7e10");

        // Act
        var send = client.SendAsync(request, jobService.ClientAbort.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        var cancelledJobId = await jobService.Cancelled.WaitAsync(SignalTimeout);
        await uploads.Finished.WaitAsync(SignalTimeout);

        // Assert
        var created = Assert.Single(jobService.Created);
        Assert.Equal(created.JobId, cancelledJobId);
        Assert.Equal("0d6f1e3a-5a52-4f4b-9a3e-2f6a1c9b7e10", created.ClientUploadId);
        Assert.Empty(jobService.Queued);
    }

    [AzuriteFact]
    public async Task UploadEpisode_DoesNotRequestAiTitle_WhenUntitledUploadIsAbortedAfterJobEntityIsCreated()
    {
        // Arrange
        var jobService = new RecordingJobService { AbortOnCreate = true };
        var aiService = new CountingAiService();
        var uploads = new UploadRequestTracker();
        using var factory = CreateFactory(jobService, uploads, aiService);
        using var client = factory.CreateClient();
        await CreateTestFeedAsync(client);
        using var request = CreateUploadRequest("aborted-untitled.mp3", clientUploadId: null, title: null);

        // Act
        var send = client.SendAsync(request, jobService.ClientAbort.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await jobService.Cancelled.WaitAsync(SignalTimeout);
        await uploads.Finished.WaitAsync(SignalTimeout);

        // Assert
        Assert.Equal(0, aiService.Calls);
        Assert.Empty(jobService.Queued);
    }

    [AzuriteFact]
    public async Task UploadEpisode_StillQueuesJob_WhenRequestIsAbortedButCancelDoesNotTakeEffect()
    {
        // Arrange
        var jobService = new RecordingJobService { AbortOnCreate = true, CancelTakesEffect = false };
        var uploads = new UploadRequestTracker();
        using var factory = CreateFactory(jobService, uploads);
        using var client = factory.CreateClient();
        await CreateTestFeedAsync(client);
        using var request = CreateUploadRequest("cancel-fails.mp3", clientUploadId: "upload-cancel-fails");

        // Act
        var send = client.SendAsync(request, jobService.ClientAbort.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        var queued = await jobService.QueuedSignal.WaitAsync(SignalTimeout);
        await uploads.Finished.WaitAsync(SignalTimeout);

        // Assert
        var created = Assert.Single(jobService.Created);
        Assert.Equal(created.JobId, queued.JobId);
        Assert.True(jobService.Cancelled.IsCompleted);
    }

    [AzuriteFact]
    public async Task UploadEpisode_StoresAndReturnsClientUploadId_WhenUploadCompletes()
    {
        // Arrange
        var jobService = new RecordingJobService();
        using var factory = CreateFactory(jobService);
        using var client = factory.CreateClient();
        await CreateTestFeedAsync(client);
        using var request = CreateUploadRequest("kept.mp3", clientUploadId: "upload-1");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("upload-1", body.RootElement.GetProperty("clientUploadId").GetString());
        Assert.Equal("upload-1", Assert.Single(jobService.Created).ClientUploadId);
        Assert.Single(jobService.Queued);
        Assert.False(jobService.Cancelled.IsCompleted);
    }

    [AzuriteFact]
    public async Task RecentJobsListing_ReturnsClientUploadIdStoredOnJob()
    {
        // Arrange
        using var client = _baseFactory.CreateClient();
        await CreateTestFeedAsync(client);
        using var upload = CreateUploadRequest("listed.mp3", clientUploadId: "listed-upload-1");
        var uploadResponse = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Accepted, uploadResponse.StatusCode);
        using var uploadBody = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        var jobId = uploadBody.RootElement.GetProperty("jobId").GetString();
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/feeds/{TestFeedId}/jobs?since=1h");
        listRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var listResponse = await client.SendAsync(listRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = Assert.Single(listBody.RootElement.EnumerateArray(), job => job.GetProperty("jobId").GetString() == jobId);
        Assert.Equal("listed-upload-1", listed.GetProperty("clientUploadId").GetString());
    }

    [AzuriteFact]
    public async Task UploadEpisode_CreatesJobWithoutClientUploadId_WhenClientSendsNone()
    {
        // Arrange
        var jobService = new RecordingJobService();
        using var factory = CreateFactory(jobService);
        using var client = factory.CreateClient();
        await CreateTestFeedAsync(client);
        using var request = CreateUploadRequest("cli.mp3", clientUploadId: null);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(Assert.Single(jobService.Created).ClientUploadId);
    }

    [AzuriteFact]
    public async Task UploadEpisode_Returns400WithoutCreatingJob_WhenClientUploadIdIsMalformed()
    {
        // Arrange
        var jobService = new RecordingJobService();
        using var factory = CreateFactory(jobService);
        using var client = factory.CreateClient();
        await CreateTestFeedAsync(client);
        using var request = CreateUploadRequest("bad-id.mp3", clientUploadId: "not_valid/id");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobService.Created);
    }

    private WebApplicationFactory<FeatherPod.Server.ServerAssemblyMarker> CreateFactory(RecordingJobService jobService, UploadRequestTracker? uploads = null, IAiService? aiService = null)
    {
        var factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IJobService>();
            services.AddSingleton<IJobService>(jobService);
            services.AddHttpContextAccessor();
            if (uploads != null)
            {
                services.AddSingleton<IStartupFilter>(uploads);
            }

            if (aiService != null)
            {
                services.RemoveAll<IAiService>();
                services.AddSingleton(aiService);
            }
        }));
        jobService.HttpContextAccessor = factory.Services.GetRequiredService<IHttpContextAccessor>();

        return factory;
    }

    private static async Task CreateTestFeedAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds");
        request.Content = new StringContent($$"""{ "id": "{{TestFeedId}}", "title": "Abort Test", "description": "d", "author": "a", "email": "test@example.com", "language": "en", "category": "Technology" }""", System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateUploadRequest(string fileName, string? clientUploadId, string? title = "Abort Test Episode")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("fake audio data"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", fileName);
        if (title != null)
        {
            content.Add(new StringContent(title), "title");
        }

        if (clientUploadId != null)
        {
            content.Add(new StringContent(clientUploadId), "clientUploadId");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes?normalize=true&source=Browser") { Content = content };
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        return request;
    }

    /// <summary>
    /// Middleware at the front of the pipeline that signals once the episode upload request has finished, aborted or not,
    /// so a test asserting that the handler did not do something reads its records only after the handler has returned.
    /// </summary>
    private sealed class UploadRequestTracker : IStartupFilter
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Finished => _finished.Task;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    try
                    {
                        await nextMiddleware();
                    }
                    finally
                    {
                        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments($"/api/feeds/{TestFeedId}/episodes", StringComparison.OrdinalIgnoreCase))
                        {
                            _finished.TrySetResult();
                        }
                    }
                });
                next(app);
            };
        }
    }

    /// <summary>
    /// AI service that counts title requests and answers each with a fixed title.
    /// </summary>
    private sealed class CountingAiService : IAiService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool IsAvailable => true;

        public Task<string?> SuggestTitleAsync(string filename, string? note = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            return Task.FromResult<string?>("AI Title");
        }
    }

    /// <summary>
    /// In-memory job service that records created, queued and cancelled jobs. With <see cref="AbortOnCreate"/>, creating
    /// a job aborts the client's request and waits until the server observes the abort, so the controller resumes after
    /// entity creation with its request already aborted.
    /// </summary>
    private sealed class RecordingJobService : IJobService
    {
        private readonly InMemoryJobService _inner = new();
        private readonly TaskCompletionSource<string> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<NormalizationJob> _queued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AbortOnCreate { get; init; }

        /// <summary>
        /// When false, CancelJobAsync returns null as JobService does when its cancel did not take effect.
        /// </summary>
        public bool CancelTakesEffect { get; init; } = true;

        public CancellationTokenSource ClientAbort { get; } = new();

        public IHttpContextAccessor? HttpContextAccessor { get; set; }

        public List<CreateJobOptions> Created { get; } = [];

        public List<NormalizationJob> Queued { get; } = [];

        public Task<string> Cancelled => _cancelled.Task;

        public Task<NormalizationJob> QueuedSignal => _queued.Task;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task CreateJobStatusAsync(CreateJobOptions options, CancellationToken cancellationToken = default)
        {
            Created.Add(options);
            if (!AbortOnCreate)
            {
                return;
            }

            var requestAborted = HttpContextAccessor?.HttpContext?.RequestAborted ?? throw new InvalidOperationException("No request context");
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = requestAborted.Register(() => observed.TrySetResult());
            await ClientAbort.CancelAsync();
            await observed.Task.WaitAsync(SignalTimeout, CancellationToken.None);
        }

        public Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default)
        {
            Queued.Add(job);
            _queued.TrySetResult(job);

            return Task.CompletedTask;
        }

        public Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            _cancelled.TrySetResult(jobId);
            if (!CancelTakesEffect)
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            // A cancelled entity, so the controller's terminal publish and notification run as they would in production
            return Task.FromResult<JobStatusEntity?>(new JobStatusEntity { RowKey = jobId, FeedId = TestFeedId, Status = nameof(JobStatus.Cancelled), NormalizationStage = nameof(NormalizationStage.Cancelled) });
        }

        public Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default) => _inner.GetJobStatusAsync(jobId, cancellationToken);

        public Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default) => _inner.GetActiveJobsByFeedAsync(feedId, cancellationToken);

        public Task<List<JobStatusEntity>> GetActiveJobsByTranscriptionStatusAsync(IReadOnlyCollection<string> transcriptionStatuses, CancellationToken cancellationToken = default) =>
            _inner.GetActiveJobsByTranscriptionStatusAsync(transcriptionStatuses, cancellationToken);

        public Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default) => _inner.GetRecentJobsByFeedAsync(feedId, since, cancellationToken);

        public Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default) => _inner.UpdateJobStatusAsync(jobId, mutate, cancellationToken);

        public Task<JobStatusEntity?> MergeJobFieldsAsync(string jobId, Action<JobStatusEntity> configure, CancellationToken cancellationToken = default) => _inner.MergeJobFieldsAsync(jobId, configure, cancellationToken);

        public Task MergeWithETagAsync(string jobId, Action<JobStatusEntity> configure, Azure.ETag etag, CancellationToken cancellationToken = default) => _inner.MergeWithETagAsync(jobId, configure, etag, cancellationToken);
    }
}
