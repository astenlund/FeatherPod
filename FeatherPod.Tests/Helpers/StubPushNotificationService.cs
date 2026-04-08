using FeatherPod.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// <see cref="PushNotificationService"/> stub that takes its dependencies via the constructor
/// (rather than building throwaway ones internally) so tests can share state across services
/// when needed.
/// </summary>
public sealed class StubPushNotificationService : PushNotificationService
{
    public StubPushNotificationService(IBlobStorageService blob, EpisodeService episodes, IJobService jobs)
        : base(blob, episodes, jobs, new ConfigurationBuilder().Build(), NullLogger<PushNotificationService>.Instance)
    {
    }
}
