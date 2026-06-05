using System.Runtime.InteropServices;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

internal static class CrossPlatformShell
{
    private static bool IsWindows =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // BuildCommandRunner splits on the first space into
    // (fileName, arguments) and invokes via ProcessStartInfo with
    // UseShellExecute=false. We therefore return a string in that
    // exact shape, wrapping the platform-appropriate script body
    // because cmd and sh disagree on built-ins like `mkdir -p`.
    public static string Wrap(string posixScript, string cmdScript) =>
        IsWindows ? $"cmd /c \"{cmdScript}\"" : $"/bin/sh -c \"{posixScript}\"";

    // Convenience wrappers for the most common scripts in fixtures.
    public static string True =>
        IsWindows ? "cmd /c \"exit 0\"" : "/usr/bin/true";

    public static string ExitCode(int code) =>
        IsWindows ? $"cmd /c \"exit {code}\"" : $"/bin/sh -c \"exit {code}\"";
}
