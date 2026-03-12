using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FeatherPod.Infrastructure;

[SupportedOSPlatform("windows")]
internal static partial class HeadlessErrorHandler
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    internal static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, message, "FeatherPod", MB_OK | MB_ICONERROR);
        }
    }
}
