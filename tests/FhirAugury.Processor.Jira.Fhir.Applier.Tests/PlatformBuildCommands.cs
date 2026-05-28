using System.Collections.Generic;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests;

/// <summary>
/// Platform-aware <c>BuildCommand</c> template factory for tests that exercise
/// <c>BuildCommandRunner</c>. The runner deliberately invokes the rendered command
/// via <see cref="System.Diagnostics.ProcessStartInfo"/> without a shell wrapper, so
/// the test must supply something the host OS can actually execute. POSIX
/// <c>/bin/sh</c> / <c>/bin/true</c> templates only work on Linux/macOS; the helpers
/// below emit the equivalent on Windows using <c>cmd /c</c>.
/// </summary>
internal static class PlatformBuildCommands
{
    public static string True() =>
        OperatingSystem.IsWindows() ? "cmd /c exit 0" : "/bin/true";

    public static string ExitWithCode(int code) =>
        OperatingSystem.IsWindows()
            ? $"cmd /c exit {code}"
            : $"/bin/sh -c \"exit {code}\"";

    public static string ExitWithStderr(string message, int code) =>
        OperatingSystem.IsWindows()
            ? $"cmd /c \"echo {message} 1>&2 & exit {code}\""
            : $"/bin/sh -c \"echo {message} >&2 && exit {code}\"";

    /// <summary>
    /// Build-command template that, when executed under the current working directory,
    /// creates any missing parent directories for each file and writes the file's
    /// content. ASCII content / no-space paths only (every existing call site complies).
    /// </summary>
    public static string WriteFiles(params (string RelativePath, string Content)[] files)
    {
        if (OperatingSystem.IsWindows())
        {
            List<string> parts = [];
            HashSet<string> mkdirs = [];
            foreach ((string rel, string content) in files)
            {
                string winPath = rel.Replace('/', '\\');
                string? dir = System.IO.Path.GetDirectoryName(winPath);
                if (!string.IsNullOrEmpty(dir) && mkdirs.Add(dir))
                {
                    parts.Add($"if not exist {dir} mkdir {dir}");
                }
                parts.Add($"(echo {content})>{winPath}");
            }
            return $"cmd /c \"{string.Join(" & ", parts)}\"";
        }
        else
        {
            List<string> parts = [];
            HashSet<string> mkdirs = [];
            foreach ((string rel, string content) in files)
            {
                string? dir = System.IO.Path.GetDirectoryName(rel);
                if (!string.IsNullOrEmpty(dir) && mkdirs.Add(dir))
                {
                    parts.Add($"mkdir -p {dir}");
                }
                parts.Add($"echo {content} > {rel}");
            }
            return $"/bin/sh -c \"{string.Join(" && ", parts)}\"";
        }
    }
}
