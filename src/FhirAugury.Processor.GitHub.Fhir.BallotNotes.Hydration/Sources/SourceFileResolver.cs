using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>A source file belonging to a unit, with its role and touched flag.</summary>
public sealed record ResolvedSourceFile
{
    public required string Path { get; init; }
    public string Role { get; init; } = "Source";
    public bool TouchedInWindow { get; init; }
}

/// <summary>The resolved source files for a unit plus an optional empty-match note.</summary>
public sealed record SourceFileResolution
{
    public required IReadOnlyList<ResolvedSourceFile> Files { get; init; }

    /// <summary>Set when an expected scope matched no files at HEAD; otherwise empty.</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Resolves a unit's on-disk source files at HEAD (via <c>git ls-tree</c>) and
/// marks which were touched in the window. Artifacts list their
/// <c>source/&lt;name&gt;/</c> folder; pages resolve <c>source/&lt;name&gt;.html</c> plus any
/// touched siblings; the datatypes unit reflects its window-changed files.
/// </summary>
public static class SourceFileResolver
{
    public static async Task<SourceFileResolution> ResolveAsync(
        string clonePath,
        HydrationUnit unit,
        ISet<string> touchedPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(touchedPaths);

        return unit.Type switch
        {
            "Artifact" => await ResolveArtifactAsync(clonePath, unit, touchedPaths, ct).ConfigureAwait(false),
            "Page" => await ResolvePageAsync(clonePath, unit, touchedPaths, ct).ConfigureAwait(false),
            _ => ResolveFromChanged(unit, touchedPaths),
        };
    }

    private static async Task<SourceFileResolution> ResolveArtifactAsync(
        string clonePath, HydrationUnit unit, ISet<string> touchedPaths, CancellationToken ct)
    {
        IReadOnlyList<string> tracked = await ListTreeAsync(clonePath, $"source/{unit.Name}/", ct).ConfigureAwait(false);
        if (tracked.Count == 0)
        {
            // Fall back to the window-changed paths so nothing is lost.
            SourceFileResolution fromChanged = ResolveFromChanged(unit, touchedPaths);
            return fromChanged with
            {
                Note = $"No files tracked under source/{unit.Name}/ at HEAD; using window-changed paths.",
            };
        }

        return new SourceFileResolution { Files = BuildFiles(tracked, touchedPaths) };
    }

    private static async Task<SourceFileResolution> ResolvePageAsync(
        string clonePath, HydrationUnit unit, ISet<string> touchedPaths, CancellationToken ct)
    {
        string primary = $"source/{unit.Name}.html";
        List<string> paths = [];

        IReadOnlyList<string> tracked = await ListTreeAsync(clonePath, primary, ct).ConfigureAwait(false);
        paths.AddRange(tracked);

        foreach (string changed in unit.ChangedPaths)
        {
            if (!paths.Contains(changed, StringComparer.OrdinalIgnoreCase)) paths.Add(changed);
        }

        string note = paths.Count == 0
            ? $"Page source {primary} not found at HEAD."
            : string.Empty;

        return new SourceFileResolution { Files = BuildFiles(paths, touchedPaths), Note = note };
    }

    private static SourceFileResolution ResolveFromChanged(HydrationUnit unit, ISet<string> touchedPaths)
        => new() { Files = BuildFiles(unit.ChangedPaths, touchedPaths) };

    private static IReadOnlyList<ResolvedSourceFile> BuildFiles(IReadOnlyList<string> paths, ISet<string> touchedPaths)
    {
        List<ResolvedSourceFile> files = [];
        foreach (string path in paths)
        {
            files.Add(new ResolvedSourceFile
            {
                Path = path,
                Role = RoleFor(path),
                TouchedInWindow = touchedPaths.Contains(path),
            });
        }
        return files;
    }

    private static async Task<IReadOnlyList<string>> ListTreeAsync(string clonePath, string pathspec, CancellationToken ct)
    {
        GitRunner.GitResult result = await GitRunner.TryRunAsync(
            clonePath, ["ls-tree", "-r", "--name-only", "HEAD", "--", pathspec], ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return [];

        List<string> files = [];
        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) files.Add(trimmed);
        }
        return files;
    }

    private static string RoleFor(string path)
    {
        string lower = path.ToLowerInvariant();
        int slash = lower.LastIndexOf('/');
        string file = slash >= 0 ? lower[(slash + 1)..] : lower;

        if (lower.Contains("structuredefinition")) return "StructureDefinition";
        if (file.Contains("-introduction") || file.Contains("-intro") || file.Contains("-notes")) return "Narrative intro";
        if (lower.EndsWith(".html")) return "Narrative";
        if (lower.EndsWith(".fsh")) return "FSH";
        if (lower.EndsWith(".xml") || lower.EndsWith(".json")) return "Resource";
        return "Source";
    }
}
