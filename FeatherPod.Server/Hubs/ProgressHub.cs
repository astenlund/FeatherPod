using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace FeatherPod.Server.Hubs;

/// <summary>
/// SignalR hub for receiving real-time progress updates from the Function App.
/// The Function connects as a client and calls SendProgress; this hub publishes
/// to IJobProgressChannel (same path as the HTTP push endpoint).
/// </summary>
public class ProgressHub : Hub
{
    private readonly IJobProgressChannel _progressChannel;
    private readonly string? _internalKey;

    public ProgressHub(IJobProgressChannel progressChannel, IConfiguration configuration)
    {
        _progressChannel = progressChannel;
        _internalKey = configuration["Internal:Key"];
    }

    public override Task OnConnectedAsync()
    {
        if (!string.IsNullOrEmpty(_internalKey))
        {
            var providedKey = Context.GetHttpContext()?.Request.Query["key"].FirstOrDefault();
            if (providedKey != _internalKey)
            {
                Context.Abort();

                return Task.CompletedTask;
            }
        }

        return base.OnConnectedAsync();
    }

    public void SendProgress(string jobId, JobStatusResponse progress)
    {
        _progressChannel.Publish(jobId, progress);
    }
}
