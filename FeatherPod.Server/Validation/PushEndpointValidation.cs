using static System.StringComparison;

namespace FeatherPod.Server.Validation;

/// <summary>
/// Decides whether a Web Push subscription endpoint belongs to a known browser push service,
/// so the server never sends push requests to a host a caller chose. Covers Chrome and other
/// Chromium browsers (FCM), Firefox (Mozilla autopush), Safari (Apple) and Edge (Windows WNS).
/// </summary>
internal static class PushEndpointValidation
{
    private static readonly string[] ExactHosts = ["fcm.googleapis.com"];

    // Each suffix starts with a dot, so the bare domain itself and lookalikes such as "evil-push.apple.com" do not match.
    private static readonly string[] HostSuffixes = [".push.services.mozilla.com", ".push.apple.com", ".notify.windows.com"];

    public static bool IsAllowedEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.HostNameType != UriHostNameType.Dns
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.IdnHost;

        return ExactHosts.Any(h => host.Equals(h, OrdinalIgnoreCase))
            || HostSuffixes.Any(s => host.EndsWith(s, OrdinalIgnoreCase));
    }
}
