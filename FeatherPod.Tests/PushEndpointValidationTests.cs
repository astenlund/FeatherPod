using FeatherPod.Server.Validation;

namespace FeatherPod.Tests;

public class PushEndpointValidationTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc123")]
    [InlineData("https://fcm.googleapis.com/wp/abc123")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc123")]
    [InlineData("https://web.push.apple.com/QGabc123")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=abc123")]
    [InlineData("https://FCM.GoogleAPIs.com/fcm/send/abc123")]
    [InlineData("https://fcm.googleapis.com:443/fcm/send/abc123")]
    public void IsAllowedEndpoint_KnownPushService_ReturnsTrue(string endpoint)
    {
        // Act
        var allowed = PushEndpointValidation.IsAllowedEndpoint(endpoint);

        // Assert
        Assert.True(allowed);
    }

    [Theory]
    [InlineData("https://push.example.com/sub/abc123")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://evilfcm.googleapis.com.attacker.com/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com.evil/fcm/send/abc")]
    [InlineData("https://evil-push.apple.com/abc")]
    [InlineData("https://push.apple.com/abc")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc")]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://user@fcm.googleapis.com/fcm/send/abc")]
    [InlineData("fcm.googleapis.com/fcm/send/abc")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowedEndpoint_OtherEndpoint_ReturnsFalse(string? endpoint)
    {
        // Act
        var allowed = PushEndpointValidation.IsAllowedEndpoint(endpoint);

        // Assert
        Assert.False(allowed);
    }
}
