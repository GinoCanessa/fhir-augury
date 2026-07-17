using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using Microsoft.Extensions.FileSystemGlobbing;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

/// <summary>
/// Resolves repo-relative <c>OutputRoots</c> globs into the concrete set of files
/// (relative to the repo root) that participate in baseline snapshots and post-build
/// diffs. Returns relative paths using forward slashes for cross-platform stability.
/// </summary>
public static class OutputRootResolver
{
    public static IReadOnlyList<string> ResolveFiles(string repoRoot, IReadOnlyList<string> outputRoots)
    {
        if (!Directory.Exists(repoRoot))
        {
            return [];
        }

        Matcher matcher = new();
        foreach (string pattern in outputRoots)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            matcher.AddInclude(NormalizePattern(pattern));
        }

        Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper wrapper =
            new(new DirectoryInfo(repoRoot));
        PatternMatchingResult result = matcher.Execute(wrapper);

        List<string> files = [];
        foreach (FilePatternMatch match in result.Files)
        {
            files.Add(match.Path.Replace('\\', '/'));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string NormalizePattern(string pattern)
    {
        string trimmed = pattern.Trim();
        if (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        return trimmed.Replace('\\', '/');
    }

    public static List<string> GetEffectiveOutputRoots(ApplierRepoOptions repo)
    {
        return repo.OutputRoots.Count > 0 ? repo.OutputRoots : ["output/**"];
    }
}
