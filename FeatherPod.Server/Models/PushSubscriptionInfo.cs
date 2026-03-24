namespace FeatherPod.Server.Models;

public record PushSubscriptionInfo
{
    public required string Endpoint { get; init; }
    public required string P256dh { get; init; }
    public required string Auth { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActiveAt { get; init; }
}

public record PushSubscriptionRequest
{
    public required string Endpoint { get; init; }
    public required string P256dh { get; init; }
    public required string Auth { get; init; }
}

public record PushUnsubscribeRequest
{
    public required string Endpoint { get; init; }
}

public record PushSessionRequest
{
    public required List<string> JobIds { get; init; }
    public int UploadsRemaining { get; init; }
}
