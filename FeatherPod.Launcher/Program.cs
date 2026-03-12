using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

var exeDir = AppContext.BaseDirectory;
var featherpodExe = Path.Combine(exeDir, "featherpod.exe");

if (!File.Exists(featherpodExe))
{
    NativeInterop.ShowErrorMessageBox($"featherpod.exe not found in:\n{exeDir}");

    return 1;
}

var psi = new ProcessStartInfo
{
    FileName = featherpodExe,
    CreateNoWindow = true,
    UseShellExecute = false,
};

foreach (var arg in args)
{
    psi.ArgumentList.Add(arg);
}

try
{
    Process.Start(psi);
}
catch (Exception ex)
{
    NativeInterop.ShowErrorMessageBox($"Failed to launch featherpod.exe:\n{ex.Message}");

    return 1;
}

// Fire and forget — the CLI process runs independently (local server stays alive until idle timeout).
// The launcher exits immediately so Explorer doesn't show a "waiting" state.
return 0;

internal static partial class NativeInterop
{
    internal static void ShowErrorMessageBox(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "FeatherPod", 0x00000010 /* MB_ICONERROR */);
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
